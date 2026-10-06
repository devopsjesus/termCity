use std::{env, process::ExitCode};

fn main() -> ExitCode {
    ExitCode::from(termcity_app::run_process(env::args().skip(1)))
}
