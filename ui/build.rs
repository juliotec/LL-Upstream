fn main() {
    slint_build::compile_with_config(
        "main.slint",
        slint_build::CompilerConfiguration::new()
            .with_style("fluent-dark".into())
            .embed_resources(slint_build::EmbedResourcesKind::EmbedFiles),
    )
    .expect("compile the launcher interface");
}
