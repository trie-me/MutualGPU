# MutualGPU operating boundaries

- MutualGPU has no Backblaze/B2 integration; all production storage is AWS S3. Never access or use Backblaze/B2, including for read-only diagnostics, migrations, fallbacks, or historical checks.
- Perform every MutualGPU AWS CLI and AWS control-plane operation with the explicit `--profile ai-quinn` argument. Never use another AWS profile, account, or resource context.
- Resolve live storage and service resources from the current CloudFormation/ECS configuration. Do not infer production targets from legacy configuration fields, documentation, or adapters.
- Treat cloud diagnostics as read-only by default. Do not create, overwrite, copy, move, or delete cloud data unless the user explicitly requests that mutation.
- Never print credentials, secret values, session tokens, provider passcodes, or administrator passwords into tool output or responses.
