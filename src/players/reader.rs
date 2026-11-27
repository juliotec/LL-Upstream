#[cfg(windows)]
use super::nfc;
use anyhow::{Context, Result, bail, ensure};
#[cfg(windows)]
use std::path::Path;
use std::{
    sync::atomic::{AtomicBool, Ordering},
    time::{Duration, Instant},
};

/// The controller's NFC service only accepts commands in this enum here. A response
/// starts with its eight-byte service header, followed by the command's payload.
#[derive(Clone, Copy)]
#[repr(u8)]
enum Request {
    Start = 3,
    Stop = 4,
    State = 5,
    Read = 6,
    Buffer = 0x15,
}

trait Reader {
    fn exchange(&mut self, command: &[u8]) -> Result<Vec<u8>>;
}
fn command(device: &mut impl Reader, op: Request, data: &[u8]) -> Result<Vec<u8>> {
    let mut request = vec![
        1,
        0x91,
        matches!(op, Request::Buffer) as u8,
        op as u8,
        0,
        data.len() as u8,
        0,
        0,
    ];
    request.extend(data);
    device.exchange(&request)
}
fn state(device: &mut impl Reader) -> Result<u8> {
    command(device, Request::State, &[])?
        .get(8)
        .copied()
        .context("The controller’s reply was incomplete.")
}
fn read_buffer(device: &mut impl Reader) -> Result<[u8; 540]> {
    let mut data = Vec::with_capacity(600);
    while data.len() <= 4096 {
        let reply = command(device, Request::Buffer, &(data.len() as u16).to_le_bytes())?;
        let part = reply
            .get(8..)
            .context("The controller’s reply was incomplete.")?;
        let count = usize::from(
            *part
                .get(1)
                .context("The controller’s reply was incomplete.")?,
        );
        ensure!(
            count <= 89 && (count > 0 || part[0] == 1),
            "The controller returned an invalid page buffer."
        );
        data.extend_from_slice(
            part.get(3..3 + count)
                .context("The scan was interrupted.")?,
        );
        if part[0] == 1 {
            return data
                .get(60..600)
                .context("The controller could not read the whole tag.")?
                .try_into()
                .map_err(Into::into);
        }
    }
    bail!("The controller did not finish reading the tag.")
}
fn scan(device: &mut impl Reader, cancel: &AtomicBool) -> Result<[u8; 540]> {
    let wait = |ms| -> Result<()> {
        ensure!(!cancel.load(Ordering::Relaxed), "Scan cancelled.");
        std::thread::sleep(Duration::from_millis(ms));
        ensure!(!cancel.load(Ordering::Relaxed), "Scan cancelled.");
        Ok(())
    };
    fn look(device: &mut impl Reader, wait: &impl Fn(u64) -> Result<()>) -> Result<()> {
        command(device, Request::Stop, &[])?;
        wait(120)?;
        command(device, Request::Start, &[0, 0xe8, 3, 0x2c, 1])?;
        Ok(())
    }
    let result = (|| {
        for first in [true, false] {
            look(device, &wait)?;
            let until = Instant::now() + Duration::from_secs(if first { 10 } else { 1 });
            loop {
                ensure!(
                    Instant::now() < until,
                    "No tag found. Hold it still on the controller’s logo and try again."
                );
                wait(100)?;
                match state(device)? {
                    9 => break,
                    0 | 7 => look(device, &wait)?,
                    _ => {}
                }
            }
            let read = [
                0xd0,
                7,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                first as u8,
                3,
                0,
                0x3b,
                0x3c,
                0x77,
                0x78,
                0x86,
                0,
                0,
            ];
            command(device, Request::Read, &read)?;
            let until = Instant::now() + Duration::from_secs(2);
            loop {
                ensure!(Instant::now() < until, "The tag did not finish reading.");
                wait(50)?;
                match state(device)? {
                    4 => return read_buffer(device),
                    7 => break,
                    _ => {}
                }
            }
        }
        bail!("The tag could not be read. Hold it still on the logo and try again.")
    })();
    let _ = command(device, Request::Stop, &[]);
    result
}

#[cfg(windows)]
pub fn scan_to(folder: &Path, cancel: &AtomicBool) -> Result<String> {
    let mut controller = usb::Controller::open()?;
    let tag = scan(&mut controller, cancel)?;
    let uid = tag[..3]
        .iter()
        .chain(&tag[4..8])
        .map(|b| format!("{b:02X}"))
        .collect::<String>();
    let name = format!("Scanned {uid}.bin");
    let temp = tempfile::tempdir()?;
    let path = temp.path().join(&name);
    std::fs::write(&path, tag)?;
    nfc::add(folder, &path)?;
    Ok(format!("Added {name}."))
}

#[cfg(windows)]
mod usb {
    use super::Reader;
    use anyhow::{Context, Result, bail, ensure};
    use std::{
        fs::{File, OpenOptions},
        os::windows::{fs::OpenOptionsExt, io::AsRawHandle},
        ptr,
    };
    use windows_sys::{
        Win32::{
            Devices::{DeviceAndDriverInstallation::*, Usb::*},
            Storage::FileSystem::FILE_FLAG_OVERLAPPED,
        },
        core::GUID,
    };
    use winreg::{RegKey, enums::HKEY_LOCAL_MACHINE};

    pub(super) struct Controller {
        usb: WINUSB_INTERFACE_HANDLE,
        _file: File,
        input: u8,
        output: u8,
    }
    impl Drop for Controller {
        fn drop(&mut self) {
            unsafe {
                WinUsb_Free(self.usb);
            }
        }
    }

    fn guids() -> Vec<u128> {
        let mut ids = vec![0x6f13725e_ef0e_4fd3_ae5f_b2de989ec825];
        let hklm = RegKey::predef(HKEY_LOCAL_MACHINE);
        let Ok(usb) = hklm.open_subkey(r"SYSTEM\CurrentControlSet\Enum\USB") else {
            return ids;
        };
        for device in usb
            .enum_keys()
            .flatten()
            .filter(|s| s.to_uppercase().contains("VID_057E&PID_2069&MI_01"))
        {
            let Ok(device) = usb.open_subkey(device) else {
                continue;
            };
            for instance in device.enum_keys().flatten() {
                let Ok(settings) = device.open_subkey(format!(r"{instance}\Device Parameters"))
                else {
                    continue;
                };
                for name in ["DeviceInterfaceGUID", "DeviceInterfaceGUIDs"] {
                    let values = settings
                        .get_value::<Vec<String>, _>(name)
                        .or_else(|_| settings.get_value::<String, _>(name).map(|v| vec![v]))
                        .unwrap_or_default();
                    for value in values {
                        if let Ok(id) = u128::from_str_radix(
                            &value.trim().trim_matches(['{', '}']).replace('-', ""),
                            16,
                        ) && !ids.contains(&id)
                        {
                            ids.push(id);
                        }
                    }
                }
            }
        }
        ids
    }
    fn paths() -> Vec<String> {
        let mut paths = Vec::new();
        for id in guids() {
            let guid = GUID::from_u128(id);
            let mut len = 0;
            // Configuration Manager supplies the exact UTF-16 multi-string buffer length.
            unsafe {
                if CM_Get_Device_Interface_List_SizeW(
                    &mut len,
                    &guid,
                    ptr::null(),
                    CM_GET_DEVICE_INTERFACE_LIST_PRESENT,
                ) != CR_SUCCESS
                    || len <= 1
                {
                    continue;
                }
                let mut buf = vec![0; len as usize];
                if CM_Get_Device_Interface_ListW(
                    &guid,
                    ptr::null(),
                    buf.as_mut_ptr(),
                    len,
                    CM_GET_DEVICE_INTERFACE_LIST_PRESENT,
                ) != CR_SUCCESS
                {
                    continue;
                }
                for s in buf.split(|&c| c == 0) {
                    let p = String::from_utf16_lossy(s);
                    if p.to_uppercase().contains("VID_057E&PID_2069") && !paths.contains(&p) {
                        paths.push(p);
                    }
                }
            }
        }
        paths
    }
    impl Controller {
        pub(super) fn open() -> Result<Self> {
            let mut last = None;
            for path in paths() {
                match Self::open_path(&path) {
                    Ok(c) => return Ok(c),
                    Err(e) => last = Some(e),
                }
            }
            Err(last.unwrap_or_else(|| {
                anyhow::anyhow!(
                    "Connect a Switch 2 Pro Controller over USB and close other programs using it."
                )
            }))
        }
        fn open_path(path: &str) -> Result<Self> {
            let file = OpenOptions::new()
                .read(true)
                .write(true)
                .custom_flags(FILE_FLAG_OVERLAPPED)
                .open(path)?;
            let mut usb = ptr::null_mut();
            // The WinUSB handle borrows the open File. Controller::drop releases it before File closes.
            unsafe {
                ensure!(
                    WinUsb_Initialize(file.as_raw_handle(), &mut usb) != 0,
                    "Could not open the controller USB interface: {}",
                    std::io::Error::last_os_error()
                );
                let mut c = Controller {
                    usb,
                    _file: file,
                    input: 0,
                    output: 0,
                };
                let mut descriptor = USB_INTERFACE_DESCRIPTOR::default();
                ensure!(
                    WinUsb_QueryInterfaceSettings(usb, 0, &mut descriptor) != 0,
                    "Could not query the controller’s USB interface."
                );
                for i in 0..descriptor.bNumEndpoints {
                    let mut pipe = WINUSB_PIPE_INFORMATION::default();
                    if WinUsb_QueryPipe(usb, 0, i, &mut pipe) == 0
                        || pipe.PipeType != UsbdPipeTypeBulk
                    {
                        continue;
                    }
                    if pipe.PipeId & 0x80 != 0 {
                        if c.input == 0 {
                            c.input = pipe.PipeId;
                        }
                    } else if c.output == 0 {
                        c.output = pipe.PipeId;
                    }
                }
                ensure!(
                    c.input != 0 && c.output != 0,
                    "The controller interface has no bulk pipes."
                );
                for pipe in [c.input, c.output] {
                    let ms = 500u32;
                    ensure!(
                        WinUsb_SetPipePolicy(
                            usb,
                            pipe,
                            PIPE_TRANSFER_TIMEOUT,
                            4,
                            ptr::from_ref(&ms).cast()
                        ) != 0,
                        "Could not set the controller timeout."
                    );
                }
                WinUsb_FlushPipe(usb, c.input);
                Ok(c)
            }
        }
    }
    impl Reader for Controller {
        fn exchange(&mut self, command: &[u8]) -> Result<Vec<u8>> {
            let mut done = 0;
            let mut reply = vec![0u8; 1024];
            // Both calls are synchronous. The buffers cover the requested lengths and
            // remain live throughout the calls, bounded by the pipe transfer timeout.
            unsafe {
                if WinUsb_WritePipe(
                    self.usb,
                    self.output,
                    command.as_ptr(),
                    command.len() as u32,
                    &mut done,
                    ptr::null(),
                ) == 0
                {
                    return Err(std::io::Error::last_os_error())
                        .context("The controller did not take the scan command.");
                }
                ensure!(
                    done == command.len() as u32,
                    "The controller took an incomplete command."
                );
                if WinUsb_ReadPipe(
                    self.usb,
                    self.input,
                    reply.as_mut_ptr(),
                    reply.len() as u32,
                    &mut done,
                    ptr::null(),
                ) == 0
                {
                    bail!(
                        "The controller did not answer: {}",
                        std::io::Error::last_os_error()
                    );
                }
            }
            reply.truncate(done as usize);
            Ok(reply)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    struct Fake {
        calls: Vec<u8>,
        bytes: Vec<u8>,
        reading: bool,
        bad_size: bool,
    }
    impl Reader for Fake {
        fn exchange(&mut self, c: &[u8]) -> Result<Vec<u8>> {
            self.calls.push(c[3]);
            let mut reply = vec![0; 8];
            match c[3] {
                3 => self.reading = false,
                6 => self.reading = true,
                5 => reply.push(if self.reading { 4 } else { 9 }),
                0x15 => {
                    let offset = usize::from(u16::from_le_bytes([c[8], c[9]]));
                    let end = (offset + 89).min(self.bytes.len());
                    reply.extend([
                        (end == self.bytes.len()) as u8,
                        if self.bad_size {
                            255
                        } else {
                            (end - offset) as u8
                        },
                        0,
                    ]);
                    reply.extend(&self.bytes[offset..end]);
                }
                4 => {}
                _ => bail!("Unexpected command"),
            }
            Ok(reply)
        }
    }
    #[test]
    fn returns_only_tag_payload_and_turns_reader_off_on_error() -> Result<()> {
        let mut data = vec![1; 60];
        data.extend([7; 540]);
        let mut device = Fake {
            calls: vec![],
            bytes: data,
            reading: false,
            bad_size: false,
        };
        assert_eq!(scan(&mut device, &AtomicBool::new(false))?, [7; 540]);
        assert_eq!(device.calls.last(), Some(&4));
        device.bad_size = true;
        assert!(scan(&mut device, &AtomicBool::new(false)).is_err());
        assert_eq!(device.calls.last(), Some(&4));
        Ok(())
    }
}
