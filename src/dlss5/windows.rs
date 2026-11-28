//! (Windows) What NVIDIA's check says of a file, and this PC's display adapters
use super::{Gpu, NvidiaCheck};
use std::{ffi::c_void, io, os::windows::ffi::OsStrExt, path::Path, ptr};
use windows_sys::Win32::{
    Foundation::{HANDLE, INVALID_HANDLE_VALUE},
    Security::{
        Cryptography::{CERT_NAME_ATTR_TYPE, CertGetNameStringW, szOID_ORGANIZATION_NAME},
        WinTrust::{
            WINTRUST_ACTION_GENERIC_VERIFY_V2, WINTRUST_DATA, WINTRUST_DATA_0, WINTRUST_FILE_INFO,
            WTD_CACHE_ONLY_URL_RETRIEVAL, WTD_CHOICE_FILE, WTD_REVOCATION_CHECK_NONE,
            WTD_REVOKE_NONE, WTD_STATEACTION_CLOSE, WTD_STATEACTION_VERIFY, WTD_UI_NONE,
            WTHelperGetProvCertFromChain, WTHelperGetProvSignerFromChain,
            WTHelperProvDataFromStateData, WinVerifyTrust,
        },
    },
    Storage::FileSystem::{GetFileVersionInfoSizeW, GetFileVersionInfoW, VerQueryValueW},
};

/// The organisation NVIDIA signs its files as
const NVIDIA: &str = "NVIDIA Corporation";

/// Whether path is NVIDIA's DLSS 5 file. NVIDIA's runtime loads only a file NVIDIA signed, so it must carry a valid embedded
/// signature whose signer's organisation is NVIDIA Corporation (as the Bloodborne PC port's launcher checks its model), and
/// its description must name the DLSS 5 model (DLSSNR), not another of NVIDIA's DLLs
pub(crate) fn nvidia_file(path: &Path) -> NvidiaCheck {
    if signer_organization(path).as_deref() != Some(NVIDIA) {
        return NvidiaCheck::NotNvidia;
    }
    match description(path) {
        Some(text) if text.to_ascii_uppercase().contains("DLSSNR") => NvidiaCheck::DlssModel,
        _ => NvidiaCheck::OtherFile,
    }
}

/// The organisation (the O attribute alone, not text in another one) of the certificate that signed path, when Windows finds
/// its embedded signature valid; none otherwise. Offline: revocation is not checked and nothing is downloaded
pub(crate) fn signer_organization(path: &Path) -> Option<String> {
    let name = wide(path);
    let mut file = WINTRUST_FILE_INFO {
        cbStruct: size_of::<WINTRUST_FILE_INFO>() as u32,
        pcwszFilePath: name.as_ptr(),
        hFile: ptr::null_mut(),
        pgKnownSubject: ptr::null_mut(),
    };
    let mut data = WINTRUST_DATA {
        cbStruct: size_of::<WINTRUST_DATA>() as u32,
        dwUIChoice: WTD_UI_NONE,
        fdwRevocationChecks: WTD_REVOKE_NONE,
        dwUnionChoice: WTD_CHOICE_FILE,
        Anonymous: WINTRUST_DATA_0 { pFile: &mut file },
        dwStateAction: WTD_STATEACTION_VERIFY,
        dwProvFlags: WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_REVOCATION_CHECK_NONE,
        ..Default::default()
    };
    let mut action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
    // (no window: INVALID_HANDLE_VALUE; data and file outlive both calls, and the state the first call opens is closed by the
    // second whatever the first said)
    let verified =
        unsafe { WinVerifyTrust(INVALID_HANDLE_VALUE, &mut action, (&raw mut data).cast()) } == 0;
    let organization = if verified {
        unsafe { organization_of(data.hWVTStateData) }
    } else {
        None
    };
    data.dwStateAction = WTD_STATEACTION_CLOSE;
    unsafe { WinVerifyTrust(INVALID_HANDLE_VALUE, &mut action, (&raw mut data).cast()) };
    organization
}

/// The O attribute of the first signer's own certificate in a verified signature's state
///
/// # Safety
/// state is the open state of a WinVerifyTrust call that verified the file, not yet closed
unsafe fn organization_of(state: HANDLE) -> Option<String> {
    let provider = unsafe { WTHelperProvDataFromStateData(state) };
    if provider.is_null() {
        return None;
    }
    let signer = unsafe { WTHelperGetProvSignerFromChain(provider, 0, 0, 0) };
    if signer.is_null() {
        return None;
    }
    let certificate = unsafe { WTHelperGetProvCertFromChain(signer, 0) };
    if certificate.is_null() {
        return None;
    }
    let context = unsafe { (*certificate).pCert };
    if context.is_null() {
        return None;
    }
    let oid = szOID_ORGANIZATION_NAME.cast::<c_void>();
    // (the length with its terminating zero; 1 when the subject has no O attribute)
    let length =
        unsafe { CertGetNameStringW(context, CERT_NAME_ATTR_TYPE, 0, oid, ptr::null_mut(), 0) };
    if length <= 1 {
        return None;
    }
    let mut text = vec![0u16; length as usize];
    let written = unsafe {
        CertGetNameStringW(
            context,
            CERT_NAME_ATTR_TYPE,
            0,
            oid,
            text.as_mut_ptr(),
            length,
        )
    };
    text.truncate(written.saturating_sub(1) as usize);
    String::from_utf16(&text)
        .ok()
        .filter(|name| !name.is_empty())
}

/// A file's description (its version resource's FileDescription, in the first language it lists, else US English); none
/// when it has none
pub(crate) fn description(path: &Path) -> Option<String> {
    let name = wide(path);
    let mut ignored = 0u32;
    let size = unsafe { GetFileVersionInfoSizeW(name.as_ptr(), &mut ignored) };
    if size == 0 {
        return None;
    }
    // (u32s: the resource's values are aligned to 4 bytes within it, so the block is too)
    let mut block = vec![0u32; (size as usize).div_ceil(4)];
    if unsafe { GetFileVersionInfoW(name.as_ptr(), 0, size, block.as_mut_ptr().cast()) } == 0 {
        return None;
    }
    let query = |sub: &str| -> Option<(*const c_void, u32)> {
        let sub: Vec<u16> = sub.encode_utf16().chain([0]).collect();
        let mut value = ptr::null_mut();
        let mut length = 0u32;
        let found =
            unsafe { VerQueryValueW(block.as_ptr().cast(), sub.as_ptr(), &mut value, &mut length) }
                != 0;
        (found && !value.is_null() && length > 0).then_some((value.cast_const(), length))
    };
    let mut languages: Vec<(u16, u16)> = Vec::new();
    if let Some((value, bytes)) = query(r"\VarFileInfo\Translation") {
        // (pairs of language and code page, u16 each; the pointer is into block, which lives on)
        let words = unsafe { std::slice::from_raw_parts(value.cast::<u16>(), bytes as usize / 2) };
        languages.extend(
            words
                .as_chunks::<2>()
                .0
                .iter()
                .map(|&[language, code_page]| (language, code_page)),
        );
    }
    languages.extend([(0x0409, 0x04b0), (0x0409, 0x04e4), (0x0409, 0x0000)]);
    languages.into_iter().find_map(|(language, code_page)| {
        let (value, chars) = query(&format!(
            r"\StringFileInfo\{language:04x}{code_page:04x}\FileDescription"
        ))?;
        let text = unsafe { std::slice::from_raw_parts(value.cast::<u16>(), chars as usize) };
        let end = text.iter().position(|&c| c == 0).unwrap_or(text.len());
        String::from_utf16(&text[..end])
            .ok()
            .map(|text| text.trim().to_owned())
            .filter(|text| !text.is_empty())
    })
}

/// This PC's display adapters, from the registry's display class: each one's maker (MatchingDeviceId's `ven_`) and name
/// (DriverDesc). An adapter of a maker this does not know (Microsoft's basic display adapter, remote displays) is left out
pub(crate) fn gpus() -> Vec<Gpu> {
    use winreg::{RegKey, enums::HKEY_LOCAL_MACHINE};
    const DISPLAY_CLASS: &str =
        r"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    let mut found = Vec::new();
    let Ok(class) = RegKey::predef(HKEY_LOCAL_MACHINE).open_subkey(DISPLAY_CLASS) else {
        return found;
    };
    let mut adapters: Vec<String> = class
        .enum_keys()
        .filter_map(Result::ok)
        .filter(|name| name.len() == 4 && name.bytes().all(|b| b.is_ascii_digit()))
        .collect();
    adapters.sort();
    for adapter in adapters {
        let Ok(key) = class.open_subkey(&adapter) else {
            continue;
        };
        let Ok(id) = key.get_value::<String, _>("MatchingDeviceId") else {
            continue;
        };
        let name = key.get_value::<String, _>("DriverDesc").ok();
        if let Some(gpu) = adapter_of(&id, name.as_deref())
            && !found.contains(&gpu)
        {
            found.push(gpu);
        }
    }
    found
}

/// An adapter by its MatchingDeviceId (`pci\ven_10de&dev_2782…`) and name: none for a maker this does not know
pub(crate) fn adapter_of(device_id: &str, name: Option<&str>) -> Option<Gpu> {
    let lower = device_id.to_ascii_lowercase();
    let at = lower.find("ven_")? + 4;
    let vendor = lower.get(at..at + 4)?;
    let nvidia = match vendor {
        "10de" => true,
        "1002" | "8086" => false,
        _ => return None,
    };
    Some(Gpu {
        nvidia,
        name: name
            .map(str::trim)
            .filter(|name| !name.is_empty())
            .map(str::to_owned),
    })
}

fn wide(path: &Path) -> Vec<u16> {
    path.as_os_str().encode_wide().chain([0]).collect()
}

/// A mutex named for the DLSS 5 files, shared by every launcher of this user (a portable one and an installed one use the
/// same folder): held while one of them adds, removes or settles the record. Released when dropped, on the thread that took
/// it (the handle is not Send)
pub(crate) struct NamedLock(windows_sys::Win32::Foundation::HANDLE);

impl NamedLock {
    const NAME: &'static str = r"Local\LongshotLauncher.Dlss5Files";

    /// The lock: waited for (wait), or none while another launcher holds it
    pub(crate) fn acquire(wait: bool) -> io::Result<Option<NamedLock>> {
        use windows_sys::Win32::{
            Foundation::{CloseHandle, WAIT_ABANDONED, WAIT_FAILED, WAIT_OBJECT_0, WAIT_TIMEOUT},
            System::Threading::{CreateMutexW, INFINITE, WaitForSingleObject},
        };
        let name: Vec<u16> = Self::NAME.encode_utf16().chain([0]).collect();
        let handle = unsafe { CreateMutexW(ptr::null(), 0, name.as_ptr()) };
        if handle.is_null() {
            return Err(io::Error::last_os_error());
        }
        // (abandoned: a launcher that held it ended without releasing it; the record is settled before any use)
        match unsafe { WaitForSingleObject(handle, if wait { INFINITE } else { 0 }) } {
            WAIT_OBJECT_0 | WAIT_ABANDONED => Ok(Some(NamedLock(handle))),
            result => {
                let error = if result == WAIT_FAILED {
                    Some(io::Error::last_os_error())
                } else if result == WAIT_TIMEOUT && !wait {
                    None
                } else {
                    Some(io::Error::other(
                        "Unexpected result waiting for the DLSS 5 mutex",
                    ))
                };
                unsafe { CloseHandle(handle) };
                match error {
                    Some(error) => Err(error),
                    None => Ok(None),
                }
            }
        }
    }
}

impl Drop for NamedLock {
    fn drop(&mut self) {
        use windows_sys::Win32::{Foundation::CloseHandle, System::Threading::ReleaseMutex};
        unsafe {
            ReleaseMutex(self.0);
            CloseHandle(self.0);
        }
    }
}
