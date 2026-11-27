//! Review the selected builder's pending code grants before handing it a build or play request.
use super::{
    jobs::{Progress, SharedWork, worker},
    launch, process, versions, wizard,
};
use crate::UiMutexExt as _;
use crate::{Game, MainWindow, Page, mods::trust, players};
use anyhow::{Context, Result, ensure};
use slint::ComponentHandle;
use std::path::{Path, PathBuf};

#[derive(Clone, Copy, PartialEq)]
enum Operation {
    Review,
    Build,
    Play,
}

#[derive(PartialEq)]
pub(super) struct Request {
    operation: Operation,
    page: Page,
    profile: PathBuf,
    builder: PathBuf,
}

pub(super) struct Review {
    request: Request,
    grants: Vec<trust::Grant>,
}

impl Request {
    fn selected(
        operation: Operation,
        ui: &MainWindow,
        home: &Path,
        work: &SharedWork,
    ) -> Result<Self> {
        let profile = players::current_dir(home)?;
        let builder = match operation {
            Operation::Play => versions::selected(home, None)?
                .folder
                .join(process::executable()),
            Operation::Review | Operation::Build => {
                ensure!(
                    super::autocheck::matches_prepared(ui, work),
                    "Wait for the selected game files to be checked."
                );
                let state = work.guard();
                let prepared = state
                    .prepared
                    .as_ref()
                    .context("Check your game files first.")?;
                ensure!(
                    prepared.ctx.profile == profile,
                    "The active profile changed. Check the game files again."
                );
                prepared.exe.clone()
            }
        };
        Ok(Self {
            operation,
            page: ui.get_page(),
            profile,
            builder,
        })
    }

    fn validate(&self, ui: &MainWindow, home: &Path, work: &SharedWork) -> Result<()> {
        ensure!(
            *self == Self::selected(self.operation, ui, home, work)?,
            "The selected game or profile changed. Try again."
        );
        Ok(())
    }

    fn grants(&self, home: &Path, approval: Option<&[trust::Grant]>) -> Result<Vec<trust::Grant>> {
        if let Some(grants) = approval {
            trust::approve_all(home, &self.builder, &self.profile, grants)?;
            return Ok(Vec::new());
        }
        trust::pending(home, &self.builder, &self.profile)
    }

    fn check(
        self,
        ui: &MainWindow,
        home: &Path,
        work: &SharedWork,
        approval: Option<Vec<trust::Grant>>,
    ) {
        let root = home.to_owned();
        worker(ui, home, work, move |cancel, _| {
            cancel.check()?;
            let next = self.grants(&root, approval.as_deref());
            cancel.check()?;
            Ok(Progress::ModTrust(self, next))
        });
    }
}

fn begin(ui: &MainWindow, home: &Path, work: &SharedWork, operation: Operation) -> Result<()> {
    let g = ui.global::<Game>();
    if g.get_busy()
        || (operation == Operation::Play && g.get_playing())
        || !g.get_mod_trust_review().is_empty()
    {
        return Ok(());
    }
    Request::selected(operation, ui, home, work)?.check(ui, home, work, None);
    Ok(())
}

pub(super) fn play(ui: &MainWindow, home: &Path, work: &SharedWork) -> Result<()> {
    begin(ui, home, work, Operation::Play)
}

pub(super) fn review(ui: &MainWindow, home: &Path, work: &SharedWork) -> Result<()> {
    begin(ui, home, work, Operation::Review)
}

pub(super) fn build(ui: &MainWindow, home: &Path, work: &SharedWork) -> Result<()> {
    begin(ui, home, work, Operation::Build)
}

pub(super) fn ready(
    ui: &MainWindow,
    home: &Path,
    work: &SharedWork,
    request: Request,
    grants: Result<Vec<trust::Grant>>,
) -> Result<()> {
    work.guard().cancel.check()?;
    request.validate(ui, home, work)?;
    let grants = grants?;
    if request.operation != Operation::Play {
        ui.global::<Game>()
            .set_mod_review_needed(!grants.is_empty());
    }
    if !grants.is_empty() {
        ui.global::<Game>()
            .set_mod_trust_review(trust::descriptions(&grants).into());
        let mut state = work.guard();
        if request.operation == Operation::Play {
            state.finished_build = None;
        }
        state.mod_review = Some(Review { request, grants });
        return Ok(());
    }
    match request.operation {
        Operation::Play => launch::play(
            ui,
            home,
            work,
            request
                .builder
                .parent()
                .context("The builder has no version folder.")?,
            &request.profile,
        ),
        Operation::Review => {
            ui.global::<Game>()
                .set_status("All mod permissions are up to date.".into());
            Ok(())
        }
        Operation::Build => wizard::build(ui, home, work),
    }
}

pub(super) fn approve(ui: &MainWindow, home: &Path, work: &SharedWork) -> Result<()> {
    let review = work.guard().mod_review.take();
    if let Some(review) = review {
        ui.global::<Game>().set_mod_trust_review("".into());
        review.request.validate(ui, home, work)?;
        review.request.check(ui, home, work, Some(review.grants));
    }
    Ok(())
}

pub(super) fn cancel(ui: &MainWindow, _: &Path, work: &SharedWork) -> Result<()> {
    let review = work.guard().mod_review.take();
    if review.is_some_and(|r| r.request.operation != Operation::Review) {
        ui.global::<Game>().set_play_after_build(false);
    }
    ui.global::<Game>().set_mod_trust_review("".into());
    Ok(())
}
