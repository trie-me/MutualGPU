# MutualGPU AWS operator identity

Status: retired runbook
Date: 2026-07-28

MutualGPU no longer provisions, repairs, rotates, or deletes IAM users from
this repository. The previous demo runbook used alternate bootstrap and
deployment profiles and is incompatible with the repository's current
operating boundary.

Every MutualGPU AWS CLI and AWS control-plane operation must use the existing
`ai-quinn` profile explicitly:

```bash
aws --profile ai-quinn sts get-caller-identity
```

Stop if that identity is not the intended MutualGPU account. Do not substitute
another profile, create long-lived access keys, attach broad IAM policies, or
perform IAM lifecycle operations from this runbook.

The checked-in CloudFormation templates create only the service roles required
by ECS. Current deployment and read-only resource resolution are performed by:

```bash
scripts/deploy-aws-service.sh <immutable-image-tag>
scripts/import-transactional-data.sh verify
```

Both wrappers hard-code `--profile ai-quinn`. Any future change to human
operator access requires a separately reviewed account-administration
procedure outside the MutualGPU application repository.
