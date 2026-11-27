//! The game's caption header, as documented by Longshot's control bridge and save tools.
//! Unknown layouts still permit ordinary slot management. Images only live in memory.
use super::disk;
use chrono::{NaiveDate, NaiveDateTime};
use image::{ImageFormat, ImageReader};
use slint::{Image, Rgb8Pixel, SharedPixelBuffer};
use std::{
    fs::File,
    io::{Cursor, Read},
    path::Path,
};

const HEADER: usize = 1396;
const MAX_JPEG: usize = 1024 * 1024;

#[derive(Default)]
pub struct Caption {
    pub location: String,
    pub scene: String,
    pub health: Option<(u32, u32)>,
    pub medals: Option<Vec<bool>>,
    pub stones: Option<Vec<bool>>,
    pub time: Option<NaiveDateTime>,
    pub thumbnail: Image,
}

fn word(bytes: &[u8], offset: usize) -> Option<u32> {
    Some(u32::from_le_bytes(
        bytes.get(offset..offset + 4)?.try_into().ok()?,
    ))
}

fn field(bytes: &[u8]) -> Option<&str> {
    let end = bytes.iter().position(|b| *b == 0).unwrap_or(bytes.len());
    let value = &bytes[..end];
    value
        .iter()
        .all(|b| (0x20..=0x7e).contains(b))
        .then(|| std::str::from_utf8(value).ok())?
}

// Values stored directly in the flag table are keyed by MurmurHash3 x86/32 (seed 0)
// of their public CaptionData names. Longshot's save report mirrors these exact
// fields from progress.sav. A missing or repeated key is an unknown reading.
struct Flags<'a>(&'a [u8]);
impl<'a> Flags<'a> {
    fn new(header: &'a [u8]) -> Option<Self> {
        let end = word(header, 8)? as usize;
        if !(48..=1392).contains(&end) {
            return None;
        }
        Some(Self(header.get(40..end)?))
    }
    fn value(&self, key: u32) -> Option<u32> {
        let (rows, _) = self.0.as_chunks::<8>();
        let mut matches = rows
            .iter()
            .take_while(|row| word(*row, 0) != Some(0xa3db7114))
            .filter(|row| word(*row, 0) == Some(key));
        let value = word(matches.next()?, 4)?;
        matches.next().is_none().then_some(value)
    }
    fn items<const N: usize>(&self, keys: [u32; N]) -> Option<Vec<bool>> {
        keys.into_iter()
            .map(|k| match self.value(k)? {
                0 => Some(false),
                1 => Some(true),
                _ => None,
            })
            .collect()
    }
    fn saved_at(&self) -> Option<NaiveDateTime> {
        let [year, month, day, hour, minute, second] = [
            0x9811a3f7, 0xdfd840d3, 0xbd46f485, 0x23f3d75e, 0x27853bf7, 0x5ee50bed,
        ]
        .map(|key| self.value(key));
        NaiveDate::from_ymd_opt(year?.try_into().ok()?, month?, day?)?
            .and_hms_opt(hour?, minute?, second?)
    }
}
fn parse(header: &[u8]) -> Option<(Caption, usize)> {
    if header.len() != HEADER || header[..4] != [4, 3, 2, 1] || word(header, 540)? != 1392 {
        return None;
    }
    let scene = field(&header[684..748])?;
    let map = field(&header[748..812])?;
    let location = if map.is_empty() { scene } else { map };
    let flags = Flags::new(header);
    let health = flags.as_ref().and_then(|f| {
        let current = f.value(0x1e55ecc5)?;
        let capacity = f.value(0x74bd05d0)?;
        (current <= capacity && (1..=128).contains(&capacity)).then_some((current, capacity))
    });
    Some((
        Caption {
            location: display_name(location),
            scene: display_name(scene),
            health,
            medals: flags.as_ref().and_then(|f| {
                f.items([
                    0x75bd1ecf, 0x10dc0608, 0xe1ca50c6, 0xba6a1b74, 0x89ae7032, 0x59ce596,
                ])
            }),
            stones: flags
                .as_ref()
                .and_then(|f| f.items([0xfb112756, 0x50c6bedf, 0x2f61ab71])),
            time: flags.as_ref().and_then(Flags::saved_at),
            thumbnail: Image::default(),
        },
        word(header, 1392)? as usize,
    ))
}

fn display_name(raw: &str) -> String {
    let mut out = String::new();
    let mut lower = false;
    for c in raw.chars() {
        if c.is_ascii_uppercase() && lower {
            out.push(' ');
        }
        out.push(if c == '_' { ' ' } else { c });
        lower = c.is_ascii_lowercase();
    }
    out
}

fn thumbnail(bytes: &[u8], (width, height): (u32, u32)) -> Option<Image> {
    let mut reader = ImageReader::with_format(Cursor::new(bytes), ImageFormat::Jpeg);
    let mut limits = image::Limits::default();
    limits.max_image_width = Some(1024);
    limits.max_image_height = Some(1024);
    limits.max_alloc = Some(16 * 1024 * 1024);
    reader.limits(limits);
    let decoded = reader.decode().ok()?;
    let rgb = if decoded.width() > width || decoded.height() > height {
        decoded.thumbnail(width, height).into_rgb8()
    } else {
        decoded.into_rgb8()
    };
    Some(Image::from_rgb8(
        SharedPixelBuffer::<Rgb8Pixel>::clone_from_slice(rgb.as_raw(), rgb.width(), rgb.height()),
    ))
}

pub fn read(path: &Path, thumbnail_size: Option<(u32, u32)>) -> Option<Caption> {
    disk::require_file(path).ok()?;
    let mut file = File::open(path).ok()?;
    let mut header = [0; HEADER];
    file.read_exact(&mut header).ok()?;
    let (mut caption, len) = parse(&header)?;
    if let Some(size) = thumbnail_size
        && (4..=MAX_JPEG).contains(&len)
        && file.metadata().ok()?.len() == (HEADER + len) as u64
    {
        let mut bytes = vec![0; len];
        if file.read_exact(&mut bytes).is_ok()
            && let Some(image) = thumbnail(&bytes, size)
        {
            caption.thumbnail = image;
        }
    }
    Some(caption)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn health_and_quest_flags_only_appear_when_the_caption_supplies_them() {
        let mut header = [0; HEADER];
        header[..4].copy_from_slice(&[4, 3, 2, 1]);
        header[8..12].copy_from_slice(&160u32.to_le_bytes());
        header[540..544].copy_from_slice(&1392u32.to_le_bytes());
        for (row, (key, value)) in [
            (0x1e55ecc5u32, 7u32),
            (0x74bd05d0, 12),
            (0xfb112756, 1),
            (0x50c6bedf, 0),
            (0x2f61ab71, 1),
        ]
        .into_iter()
        .enumerate()
        {
            let p = 40 + row * 8;
            header[p..p + 4].copy_from_slice(&key.to_le_bytes());
            header[p + 4..p + 8].copy_from_slice(&value.to_le_bytes());
        }
        let caption = parse(&header).unwrap().0;
        assert_eq!(caption.health, Some((7, 12)));
        assert_eq!(caption.stones, Some(vec![true, false, true]));
        assert!(caption.medals.is_none());
        header[44..48].copy_from_slice(&50u32.to_le_bytes());
        header[76..80].copy_from_slice(&3u32.to_le_bytes());
        let invalid = parse(&header).unwrap().0;
        assert!(invalid.health.is_none() && invalid.stones.is_none());
    }

    #[test]
    fn synthetic_thumbnail_decodes_without_installing_any_save_asset() {
        use image::{ExtendedColorType, codecs::jpeg::JpegEncoder};
        let mut bytes = Vec::new();
        JpegEncoder::new(&mut bytes)
            .encode(&[10, 20, 30, 40, 50, 60], 2, 1, ExtendedColorType::Rgb8)
            .unwrap();
        let image = thumbnail(&bytes, (560, 320)).unwrap();
        assert_eq!(image.size().width, 2);
        assert!(thumbnail(b"not a JPEG", (128, 84)).is_none());
    }

    #[test]
    fn caption_fields_are_layout_checked_and_dates_must_be_unambiguous() {
        let mut h = [0; HEADER];
        h[..4].copy_from_slice(&[4, 3, 2, 1]);
        h[8..12].copy_from_slice(&200u32.to_le_bytes());
        h[540..544].copy_from_slice(&1392u32.to_le_bytes());
        h[748..759].copy_from_slice(b"KokiriWoods");
        for (i, (k, v)) in [
            (0x9811a3f7u32, 2026u32),
            (0xdfd840d3, 1),
            (0xbd46f485, 1),
            (0x23f3d75e, 3),
            (0x27853bf7, 4),
            (0x5ee50bed, 0),
        ]
        .into_iter()
        .enumerate()
        {
            let pos = 40 + i * 8;
            h[pos..pos + 4].copy_from_slice(&k.to_le_bytes());
            h[pos + 4..pos + 8].copy_from_slice(&v.to_le_bytes());
        }
        let (c, _) = parse(&h).unwrap();
        assert_eq!(c.location, "Kokiri Woods");
        assert_eq!(c.time.unwrap().to_string(), "2026-01-01 03:04:00");
        h[128..132].copy_from_slice(&0x9811a3f7u32.to_le_bytes());
        assert!(parse(&h).unwrap().0.time.is_none());
        h[748] = 0xff;
        assert!(parse(&h).is_none());
    }
}
