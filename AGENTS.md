# MutualGPU operating boundaries

- Do not introduce breaking changes outside major-version releases.
- When a breaking change is necessary, produce a breaking-change notice with migration instructions before publishing the release.
- Before implementing or executing the Quinn-MutualCompute account migration, read `docs/mutualgpu-quinn-mutualcompute-migration-handoff.md` and begin with its Phase 0 verification gates.
- The Quinn-MutualCompute deployment is a clean start. Do not migrate, synchronize, dump, restore, import, or copy source PostgreSQL/S3 application data unless the user explicitly expands scope in a new reviewed plan.
- AWS S3 is the default production object-storage provider. Backblaze B2 is supported only when explicitly selected through the reviewed opt-in storage configuration; never contact B2 implicitly, as an AWS fallback, or while diagnosing an AWS-selected storage target.
- Treat `ai-quinn` as the strictly read-only source-account profile and use it only for the minimum descriptive reads required to resolve the deployed image and non-secret runtime configuration. Never use it for source mutations.
- Treat `quinn-mutualcompute-login` as an authentication-only helper profile. After each `aws login`, verify its `login_session` is exactly `arn:aws:iam::428590861908:user/mutual-ai-automation`; never use this profile for AWS service or control-plane operations.
- Treat `quinn-mutualcompute` as the target-account profile for AWS account `428590861908` in `us-east-1`. Use this profile for target provisioning and operations.
- Include an explicit `--profile ai-quinn` or `--profile quinn-mutualcompute` argument on every MutualGPU AWS CLI command; never rely on the default profile. Before target mutations, verify that `aws sts get-caller-identity --profile quinn-mutualcompute` returns account `428590861908` and ARN `arn:aws:sts::428590861908:assumed-role/MutualGPUMigrationAdmin/mutual-ai-automation`. For browser control-plane work, verify the same account ID in the AWS console before making changes.
- Resolve live storage and service resources from the current CloudFormation/ECS configuration. Do not infer production targets from legacy configuration fields, documentation, or adapters.
- Treat cloud diagnostics as read-only by default. Do not create, overwrite, copy, move, or delete cloud data unless the user explicitly requests that mutation.
- Never print credentials, secret values, session tokens, provider passcodes, or administrator passwords into tool output or responses.
