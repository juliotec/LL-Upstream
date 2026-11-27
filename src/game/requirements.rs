//! Local advisory checks from the published system requirements.
use ash::{Entry, vk};
use std::{path::Path, thread};

pub(super) struct Report {
    pub title: String,
    pub detail: String,
    pub colour: Option<slint::Color>,
}
struct Gpu {
    memory: u64,
    mesh: bool,
}

fn gpu() -> Option<Option<Gpu>> {
    // The Vulkan loader is optional. No device or window is created. Each handle below
    // belongs to this instance, which stays alive until all its physical devices are read.
    unsafe {
        let entry = Entry::load().ok()?;
        let app = vk::ApplicationInfo::default().api_version(vk::API_VERSION_1_3);
        let config = vk::InstanceCreateInfo::default().application_info(&app);
        let instance = entry.create_instance(&config, None).ok()?;
        let result = (|| {
            let devices = instance.enumerate_physical_devices().ok()?;
            let choice = devices
                .into_iter()
                .filter_map(|device| {
                    let props = instance.get_physical_device_properties(device);
                    if props.api_version < vk::API_VERSION_1_3 {
                        return None;
                    }
                    let kind = match props.device_type {
                        vk::PhysicalDeviceType::DISCRETE_GPU => 2,
                        vk::PhysicalDeviceType::INTEGRATED_GPU => 1,
                        _ => 0,
                    };
                    Some(((kind, props.driver_version), device))
                })
                .max_by_key(|(key, _)| *key);
            let info = choice.map(|(_, device)| {
                let props = instance.get_physical_device_memory_properties(device);
                let memory = props
                    .memory_heaps_as_slice()
                    .iter()
                    .filter(|heap| heap.flags.contains(vk::MemoryHeapFlags::DEVICE_LOCAL))
                    .map(|h| h.size)
                    .sum();
                let mut mesh = vk::PhysicalDeviceMeshShaderFeaturesEXT::default();
                let mut features = vk::PhysicalDeviceFeatures2::default().push_next(&mut mesh);
                instance.get_physical_device_features2(device, &mut features);
                Gpu {
                    memory,
                    mesh: mesh.mesh_shader != 0,
                }
            });
            Some(info)
        })();
        instance.destroy_instance(None);
        result
    }
}

#[cfg(target_os = "linux")]
fn memory() -> Option<u64> {
    let mem = std::fs::read_to_string("/proc/meminfo").ok()?;
    let kb = mem.lines().find_map(|line| {
        line.strip_prefix("MemTotal:")?
            .split_whitespace()
            .next()?
            .parse::<u64>()
            .ok()
    })?;
    Some((kb + (1 << 19)) >> 20)
}
#[cfg(windows)]
fn memory() -> Option<u64> {
    use windows_sys::Win32::System::SystemInformation::{GlobalMemoryStatusEx, MEMORYSTATUSEX};
    // GlobalMemoryStatusEx writes this caller-sized POD structure.
    let mut info = MEMORYSTATUSEX {
        dwLength: std::mem::size_of::<MEMORYSTATUSEX>() as u32,
        ..Default::default()
    };
    (unsafe { GlobalMemoryStatusEx(&mut info) } != 0)
        .then_some((info.ullTotalPhys + (1 << 29)) >> 30)
}
#[cfg(not(any(windows, target_os = "linux")))]
fn memory() -> Option<u64> {
    None
}

#[cfg(unix)]
pub(crate) fn disk(path: &Path) -> Option<u64> {
    use std::os::unix::ffi::OsStrExt;
    let dir = path.ancestors().find(|p| p.is_dir())?;
    let key = std::ffi::CString::new(dir.as_os_str().as_bytes()).ok()?;
    let mut stat = std::mem::MaybeUninit::<libc::statvfs>::uninit();
    // statvfs initialises the whole structure on success.
    if unsafe { libc::statvfs(key.as_ptr(), stat.as_mut_ptr()) } != 0 {
        return None;
    }
    let stat = unsafe { stat.assume_init() };
    stat.f_bavail.checked_mul(stat.f_frsize)
}
#[cfg(windows)]
pub(crate) fn disk(path: &Path) -> Option<u64> {
    use std::os::windows::ffi::OsStrExt;
    use windows_sys::Win32::Storage::FileSystem::GetDiskFreeSpaceExW;
    let path = path
        .ancestors()
        .find(|p| p.is_dir())?
        .as_os_str()
        .encode_wide()
        .chain(Some(0))
        .collect::<Vec<_>>();
    let mut available = 0;
    // GetDiskFreeSpaceExW fills only the available-bytes output used here.
    (unsafe {
        GetDiskFreeSpaceExW(
            path.as_ptr(),
            &mut available,
            std::ptr::null_mut(),
            std::ptr::null_mut(),
        )
    } != 0)
        .then_some(available)
}

pub(super) fn check() -> Report {
    let mut facts = Vec::new();
    let mut warnings = Vec::new();
    let mut failures = Vec::new();
    let mut unknown = Vec::<String>::new();
    if !cfg!(target_arch = "x86_64") {
        failures.push("Requires a 64-bit x86 PC".to_owned());
    }
    match memory() {
        Some(gb) => {
            facts.push(format!("{gb} GB RAM"));
            if gb < 16 {
                warnings.push("16 GB RAM minimum".into());
            }
        }
        None => unknown.push("RAM could not be checked".into()),
    }
    if let Ok(n) = thread::available_parallelism()
        && n.get() < 6
    {
        warnings.push(format!("{n} CPU threads; a 6-core CPU is suggested"));
    }
    match gpu() {
        Some(Some(gpu)) => {
            facts.push("Vulkan 1.3".into());
            if gpu.memory > 0 {
                facts.push(format!(
                    "{:.1} GB VRAM",
                    gpu.memory as f64 / (1u64 << 30) as f64
                ));
            }
            if gpu.memory + (256 << 20) < 4 << 30 {
                warnings.push("4 GB VRAM minimum".into());
            } else if gpu.memory + (256 << 20) < 8 << 30 {
                warnings.push("8 GB VRAM recommended".into());
            }
            if gpu.mesh {
                facts.push("Mesh shaders available".into());
            } else {
                warnings.push("No mesh shaders; using the slower compute fallback".into());
            }
        }
        Some(None) => failures.push("No Vulkan 1.3 GPU found; check your graphics driver".into()),
        None => unknown
            .push("GPU support could not be checked; requires Vulkan 1.3 and 4 GB VRAM".into()),
    }
    let (title, rgb) = if let Some(reason) = failures.first() {
        (reason.clone(), Some(0xffe2776b))
    } else if let Some(reason) = warnings.first() {
        (reason.clone(), Some(0xffe2ad63))
    } else if let Some(reason) = unknown.first() {
        (reason.clone(), Some(0xffa2a9b3))
    } else {
        ("Meets requirements".to_owned(), None)
    };
    facts.extend(failures);
    facts.extend(warnings);
    facts.extend(unknown);
    Report {
        title,
        detail: facts.join(" · "),
        colour: rgb.map(slint::Color::from_argb_encoded),
    }
}

// A first build with shader preparation measured ~2 GiB for the installation,
// ~4 GiB for build files/tools, ~2.4 GiB of prepared shaders for one graphics
// driver, and ~3.6 GiB in the shared SPIR-V store. Include room for staging.
const GIB: u64 = 1 << 30;
const CACHE_GIB: u64 = 12;
const DATA_GIB: u64 = 3;

pub(super) struct BuildSpace {
    cache: Option<u64>,
    data: Option<u64>,
    shared: bool,
}

impl BuildSpace {
    pub fn inspect(cache: &Path, data: &Path) -> Self {
        let volume = |path: &Path| {
            path.ancestors()
                .find(|p| p.is_dir())
                .and_then(crate::storage::volume)
        };
        let cache_volume = volume(cache);
        Self {
            cache: disk(cache),
            data: disk(data),
            shared: cache_volume.is_some() && cache_volume == volume(data),
        }
    }

    pub fn recommendation(&self, language: &str) -> String {
        if self.shared {
            crate::i18n::lookup(
                language,
                "Allow about {0} GiB on this drive for a first build with shaders.",
            )
            .replace("{0}", &(CACHE_GIB + DATA_GIB).to_string())
        } else {
            crate::i18n::lookup(language, "Allow about {0} GiB for Cache and {1} GiB for Longshot data on a first build with shaders.")
                .replace("{0}", &CACHE_GIB.to_string())
                .replace("{1}", &DATA_GIB.to_string())
        }
    }

    pub fn available(&self) -> String {
        let free = |n: Option<u64>| {
            n.map(|bytes| format!("{} free", crate::storage::format_size(bytes)))
                .unwrap_or_else(|| "free space unknown".into())
        };
        if self.shared {
            free(self.cache)
        } else {
            format!("Cache: {} · Data: {}", free(self.cache), free(self.data))
        }
    }

    pub fn measured(&self) -> bool {
        self.cache.is_some() && (self.shared || self.data.is_some())
    }

    pub fn below_estimate(&self) -> bool {
        if self.shared {
            self.cache.is_some_and(|n| n < (CACHE_GIB + DATA_GIB) * GIB)
        } else {
            self.cache.is_some_and(|n| n < CACHE_GIB * GIB)
                || self.data.is_some_and(|n| n < DATA_GIB * GIB)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn first_build_counts_shared_space_once_and_checks_each_separate_drive() {
        let same = BuildSpace {
            shared: true,
            cache: Some(14 * GIB),
            data: Some(14 * GIB),
        };
        assert!(same.below_estimate());
        assert_eq!(same.available(), "14.0 GiB free");
        assert!(same.recommendation("en-US").contains("15 GiB"));
        let enough = BuildSpace {
            cache: Some(15 * GIB),
            ..same
        };
        assert!(!enough.below_estimate());
        let separate = BuildSpace {
            shared: false,
            cache: Some(100 * GIB),
            data: Some(GIB),
        };
        assert!(separate.below_estimate());
        assert!(separate.available().contains("Data: 1.0 GiB free"));
        assert!(separate.recommendation("en-US").contains("12 GiB"));
        let unmeasured = BuildSpace {
            shared: false,
            cache: None,
            data: None,
        };
        assert!(!unmeasured.below_estimate());
        assert!(unmeasured.available().contains("unknown"));
    }
}
