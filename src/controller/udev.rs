//! Longshot uses the distro's steam-devices rules for raw controller access on Linux.
use anyhow::{Context, Result, bail, ensure};
use std::{
    ffi::CString,
    fs, io,
    os::unix::ffi::OsStrExt,
    path::Path,
    process::{Command, Stdio},
};

const PACKAGE: &str = "steam-devices";
const PKEXEC: &str = "/usr/bin/pkexec";

fn supported(uevent: &str) -> bool {
    fn hid_id(uevent: &str) -> Option<(u32, u32, u32)> {
        let id = uevent
            .lines()
            .find_map(|line| line.strip_prefix("HID_ID="))?;
        let (bus, id) = id.split_once(':')?;
        let (vendor, product) = id.split_once(':')?;
        let hex = |field| u32::from_str_radix(field, 16).ok();
        Some((hex(bus)?, hex(vendor)?, hex(product)?))
    }

    // USB and Bluetooth products for which Valve ships hidraw rules. Switch 2's
    // direct USB reader uses a different interface, and NFC scanning is Windows-only.
    matches!(
        hid_id(uevent),
        Some((3 | 5, 0x057e, 0x2006 | 0x2007 | 0x2009))
            | Some((3 | 5, 0x054c, 0x0268 | 0x05c4 | 0x09cc | 0x0ce6 | 0x0df2))
            | Some((3, 0x054c, 0x0ba0))
    )
}

fn denied(node: &Path) -> bool {
    let Ok(name) = CString::new(node.as_os_str().as_bytes()) else {
        return false;
    };
    // access(2) checks the actual user's device ACL without opening a controller.
    let result = unsafe { libc::access(name.as_ptr(), libc::R_OK | libc::W_OK) };
    result == -1 && io::Error::last_os_error().kind() == io::ErrorKind::PermissionDenied
}

fn needs_access(class: &Path, dev: &Path, denied: impl Fn(&Path) -> bool) -> bool {
    fs::read_dir(class).is_ok_and(|entries| {
        entries.flatten().any(|entry| {
            fs::read_to_string(entry.path().join("device/uevent"))
                .is_ok_and(|data| supported(&data))
                && denied(&dev.join(entry.file_name()))
        })
    })
}

fn rules_present(root: &Path) -> bool {
    ["etc", "run", "usr/local/lib", "usr/lib", "lib"]
        .iter()
        .any(|dir| {
            // An administrator can intentionally mask this package's rule via /dev/null.
            fs::symlink_metadata(root.join(dir).join("udev/rules.d/60-steam-input.rules")).is_ok()
        })
}

struct Installer {
    program: &'static str,
    args: &'static [&'static str],
}

impl Installer {
    fn detect() -> Option<Self> {
        if !Path::new(PKEXEC).is_file()
            || Path::new("/run/ostree-booted").exists()
            || Path::new("/.flatpak-info").exists()
        {
            return None;
        }
        [
            Self {
                program: "/usr/bin/apt-get",
                args: &["--yes", "--no-remove", "--no-install-recommends", "install"],
            },
            Self {
                program: "/usr/bin/pacman",
                args: &["--sync", "--needed", "--noconfirm"],
            },
            Self {
                program: "/usr/bin/dnf",
                args: &["--assumeyes", "install"],
            },
            Self {
                program: "/usr/bin/zypper",
                args: &["--non-interactive", "install", "--no-recommends"],
            },
        ]
        .into_iter()
        .find(|manager| {
            Path::new(manager.program).is_file()
                && (manager.program != "/usr/bin/pacman"
                    || Command::new(manager.program)
                        .args(["--sync", "--info", "--", PACKAGE])
                        .stdin(Stdio::null())
                        .stdout(Stdio::null())
                        .stderr(Stdio::null())
                        .status()
                        .is_ok_and(|status| status.success()))
        })
    }
}

pub(crate) fn applicable() -> bool {
    !rules_present(Path::new("/"))
        && needs_access(Path::new("/sys/class/hidraw"), Path::new("/dev"), denied)
        && Installer::detect().is_some()
}

pub(crate) fn install() -> Result<()> {
    // Re-check after the player clicks: the controller may have been unplugged.
    if rules_present(Path::new("/")) {
        return Ok(());
    }
    ensure!(
        needs_access(Path::new("/sys/class/hidraw"), Path::new("/dev"), denied),
        "No connected controller needs additional device permissions."
    );
    let manager =
        Installer::detect().context("This system has no supported installer for steam-devices.")?;
    let status = Command::new(PKEXEC)
        .arg("--disable-internal-agent")
        .arg(manager.program)
        .args(manager.args)
        .arg(PACKAGE)
        .current_dir("/")
        .stdin(Stdio::null())
        .status()
        .context("Could not open the system package installer.")?;
    match status.code() {
        Some(126) => bail!("Controller rule installation cancelled."),
        Some(127) => bail!("The system did not authorize installing the controller rules."),
        _ => ensure!(
            status.success(),
            "The system package manager could not install steam-devices. Check that the package is available in your software sources."
        ),
    }
    ensure!(
        rules_present(Path::new("/")),
        "The package manager finished without installing the controller rules."
    );
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn only_attached_supported_controllers_with_denied_access_need_rules() -> Result<()> {
        let tmp = tempfile::tempdir()?;
        let class = tmp.path().join("sys/class/hidraw");
        let dev = tmp.path().join("dev");
        fs::create_dir_all(class.join("hidraw3/device"))?;
        let info = class.join("hidraw3/device/uevent");
        for id in [
            "0003:0000057E:00002009",
            "0005:0000054C:00000CE6",
            "0005:0000057e:00002006",
        ] {
            fs::write(&info, format!("DRIVER=hid-generic\nHID_ID={id}\n"))?;
            assert!(needs_access(&class, &dev, |node| node == dev.join("hidraw3")));
            assert!(!needs_access(&class, &dev, |_| false));
        }
        for id in [
            "0003:0000057e:00002069",
            "0018:0000054c:00000ce6",
            "0003:0000046d:0000c534",
            "invalid",
        ] {
            fs::write(&info, format!("HID_ID={id}\n"))?;
            assert!(!needs_access(&class, &dev, |_| panic!("unrelated device")));
        }
        fs::remove_file(info)?;
        assert!(!needs_access(&class, &dev, |_| true));
        assert!(!denied(&dev.join("unplugged")));
        Ok(())
    }

    #[test]
    fn device_access_probe_distinguishes_denied_from_disconnected() -> Result<()> {
        use std::os::unix::fs::PermissionsExt;
        let tmp = tempfile::tempdir()?;
        let node = tmp.path().join("hidraw2");
        fs::write(&node, "")?;
        assert!(!denied(&node));
        fs::set_permissions(&node, fs::Permissions::from_mode(0o000))?;
        if unsafe { libc::getuid() } != 0 {
            assert!(denied(&node));
        }
        fs::remove_file(node)?;
        Ok(())
    }

    #[test]
    fn vendor_rules_and_admin_masks_prevent_reinstallation() -> Result<()> {
        let tmp = tempfile::tempdir()?;
        assert!(!rules_present(tmp.path()));
        for dir in ["usr/lib", "etc"] {
            let parent = tmp.path().join(dir).join("udev/rules.d");
            fs::create_dir_all(&parent)?;
            let rule = parent.join("60-steam-input.rules");
            if dir == "etc" {
                std::os::unix::fs::symlink("/dev/null", &rule)?;
            } else {
                fs::write(&rule, "# supplied by steam-devices")?;
            }
            assert!(rules_present(tmp.path()));
            fs::remove_file(rule)?;
        }
        Ok(())
    }
}
