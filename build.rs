fn main() {
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows") {
        embed_resource::compile("assets/windows/launcher.rc", embed_resource::NONE)
            .manifest_required()
            .expect("embed the launcher icon and manifest");
    }
}
