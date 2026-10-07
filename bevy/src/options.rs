use std::error::Error;
use std::path::PathBuf;
use std::time::{SystemTime, UNIX_EPOCH};
use termcity_core::GameConfig;

pub struct Options {
    pub config: GameConfig,
    pub smoke: bool,
    pub frames: u32,
    pub screenshot: Option<PathBuf>,
    pub license: bool,
    pub no_vsync: bool,
}

impl Options {
    pub fn parse(
        arguments: impl IntoIterator<Item = String>,
    ) -> Result<Option<Self>, Box<dyn Error>> {
        let seed = i32::from_le_bytes(
            SystemTime::now().duration_since(UNIX_EPOCH)?.subsec_nanos().to_le_bytes(),
        );
        let mut options = Self {
            config: GameConfig { seed, ..GameConfig::default() },
            smoke: false,
            frames: 360,
            screenshot: None,
            license: false,
            no_vsync: false,
        };
        let mut frames_provided = false;
        let mut args = arguments.into_iter();
        while let Some(arg) = args.next() {
            match arg.as_str() {
                "--help" | "-h" => return Ok(None),
                "--seed" => {
                    options.config.seed =
                        args.next().ok_or("--seed requires an integer")?.parse()?;
                }
                "--size" => {
                    let size = args
                        .next()
                        .ok_or("--size requires small, medium, large, or WIDTHxHEIGHT")?;
                    let (width, height) = match size.as_str() {
                        "small" => (160, 96),
                        "medium" => (320, 192),
                        "large" => (640, 384),
                        _ => {
                            let (width, height) = size.split_once('x').ok_or("invalid map size")?;
                            (width.parse()?, height.parse()?)
                        }
                    };
                    if !(80..=640).contains(&width) || !(24..=384).contains(&height) {
                        return Err("map size must be within 80x24 and 640x384".into());
                    }
                    options.config.map_width = width;
                    options.config.map_height = height;
                }
                "--smoke" => options.smoke = true,
                "--frames" => {
                    frames_provided = true;
                    options.frames = args.next().ok_or("--frames requires an integer")?.parse()?;
                    if !(300..=3600).contains(&options.frames) {
                        return Err("--frames must be between 300 and 3600".into());
                    }
                }
                "--screenshot" => {
                    options.screenshot =
                        Some(args.next().ok_or("--screenshot requires a path")?.into());
                }
                "--font-license" => options.license = true,
                "--no-vsync" => options.no_vsync = true,
                _ => return Err(format!("unknown option {arg:?}; use --help").into()),
            }
        }
        if options.screenshot.is_some() && !options.smoke {
            return Err("--screenshot requires --smoke".into());
        }
        if frames_provided && !options.smoke {
            return Err("--frames requires --smoke".into());
        }
        Ok(Some(options))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn parse(arguments: &[&str]) -> Result<Option<Options>, Box<dyn Error>> {
        Options::parse(arguments.iter().map(|arg| (*arg).to_owned()))
    }

    #[test]
    fn parses_reproducible_large_smoke() {
        let options =
            parse(&["--seed", "42", "--size", "large", "--smoke", "--frames", "600", "--no-vsync"])
                .unwrap()
                .unwrap();
        assert_eq!(options.config.seed, 42);
        assert_eq!((options.config.map_width, options.config.map_height), (640, 384));
        assert!(options.smoke);
        assert_eq!(options.frames, 600);
        assert!(options.no_vsync);
    }

    #[test]
    fn rejects_unknown_missing_and_out_of_range_arguments() {
        for args in [
            vec!["--seed"],
            vec!["--size", "1x2"],
            vec!["--frames", "2"],
            vec!["--frames", "300"],
            vec!["--wat"],
            vec!["--screenshot", "x.png"],
        ] {
            assert!(parse(&args).is_err(), "{args:?}");
        }
        assert!(parse(&["--help"]).unwrap().is_none());
    }
}
