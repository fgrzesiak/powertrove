# Recipe regression checks

Recipe tests cover independent processing by file family, passthrough inputs, preview validation, failure, cancellation and temporary-output cleanup. Desktop composition and architecture tests separately check registration and selection ranking.

Automated checks do not establish native application acceptance. On Windows, exercise the workflow with generated image, PDF and other inputs; inspect the final archive, verify output constraints and unchanged source hashes, then repeat with an already optimized input, a failing input and cancellation. See [alpha acceptance](../../../docs/release/alpha.md).
