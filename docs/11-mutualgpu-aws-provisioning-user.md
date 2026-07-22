# MutualGPU AWS provisioning user

## Purpose and authority

This runbook provisions an existing IAM deployment user using the documented `ecs-fargate` AWS CLI profile for the infrastructure in [MutualGPU AWS infrastructure provisioning](10-mutualgpu-aws-infrastructure-provisioning.md). It also retains the creation commands for rebuilding the user if necessary.

Per the demo decision, this is not a least-privilege policy. It grants full control of every AWS service expected during provisioning, including full IAM control. Because `iam:*` lets the user grant itself or another identity additional permissions, this user is effectively an account administrator. Use it only as a time-boxed deployment identity and disable or remove its access keys after the demo.

AWS recommends federated temporary credentials for human users instead of long-lived IAM access keys. This runbook deliberately uses an IAM user because that is the selected expedited workflow. MFA is still strongly recommended. See [AWS IAM security best practices](https://docs.aws.amazon.com/IAM/latest/UserGuide/best-practices.html) and [managing IAM access keys](https://docs.aws.amazon.com/IAM/latest/UserGuide/id_credentials_access-keys.html).

## 1. Permissions granted

The provisioning user receives full control over:

| Permission prefix | Purpose |
|---|---|
| `iam:*`, `sts:*` | Create ECS roles, pass roles, create service-linked roles, and manage this user's credentials |
| `ec2:*` | VPCs, subnets, routes, internet gateways, security groups, and network interfaces |
| `ecs:*`, `ecr:*` | ECS cluster/service/task definitions and container image storage |
| `elasticloadbalancing:*` | ALB, listeners, rules, target groups, and health checks |
| `acm:*` | Request and manage the `mutualgpu.com` certificate |
| `secretsmanager:*`, `kms:*` | Store deployment secrets and manage encryption |
| `cloudwatch:*`, `logs:*`, `events:*`, `sns:*` | Logs, metrics, alarms, notifications, and event rules |
| `cloudformation:*`, `s3:*`, `ssm:*` | Infrastructure deployment artifacts, state/bootstrap support, and operational parameters |
| `route53:*` | DNS management if the existing zone is in Route 53; harmlessly unused otherwise |
| `budgets:*`, `ce:*` | Budget alerts and cost visibility |
| `wafv2:*` | Optional public-demo WAF rules |
| `application-autoscaling:*` | ECS service settings, even though MVP autoscaling remains disabled |
| `servicequotas:*`, `tag:*` | Quota inspection and resource tagging |

All actions apply to all resources in the account. No permissions boundary is installed.

## 2. Prerequisites

Run the bootstrap commands with an existing administrative AWS CLI profile. Do not create or use root-user access keys. The examples assume zsh and AWS CLI v2.

Set local command variables:

```zsh
export AWS_BOOTSTRAP_PROFILE=bootstrap-admin
export AWS_REGION=us-east-1
export MUTUALGPU_IAM_POLICY=MutualGPUProvisioningFullControl
export MUTUALGPU_CLI_PROFILE=ecs-fargate
read "MUTUALGPU_IAM_USER?Existing IAM deployment user name: "
export MUTUALGPU_IAM_USER
```

Change `AWS_REGION` if the deployment will use another region. Confirm the bootstrap identity before mutating IAM:

```zsh
aws sts get-caller-identity \
  --profile "$AWS_BOOTSTRAP_PROFILE" \
  --output json
```

Stop if this is the wrong AWS account.

## 3. Create or remediate the user

### Existing deployment user

The user and its access key already exist. Do not rerun `create-user` or `create-access-key`. Using the administrator profile, confirm the user, then continue directly to the `put-user-policy` command below:

```zsh
aws iam get-user \
  --user-name "$MUTUALGPU_IAM_USER" \
  --profile "$AWS_BOOTSTRAP_PROFILE"
```

The three AWS-managed policies already attached to the deployment user may remain. The inline policy below supplies the missing full-control permissions for ACM, ALB, IAM roles, Secrets Manager, CloudWatch, budgets, and the other services in the deployment plan.

### Rebuilding a missing user

Run this only if `get-user` reports that the user does not exist:

```zsh
aws iam create-user \
  --user-name "$MUTUALGPU_IAM_USER" \
  --tags Key=Project,Value=MutualGPU Key=Purpose,Value=DemoProvisioning \
  --profile "$AWS_BOOTSTRAP_PROFILE"
```

Attach the broad service-full-control policy as an inline user policy:

```zsh
aws iam put-user-policy \
  --user-name "$MUTUALGPU_IAM_USER" \
  --policy-name "$MUTUALGPU_IAM_POLICY" \
  --policy-document '{
    "Version": "2012-10-17",
    "Statement": [
      {
        "Sid": "MutualGPURequiredServicesFullControl",
        "Effect": "Allow",
        "Action": [
          "acm:*",
          "application-autoscaling:*",
          "budgets:*",
          "ce:*",
          "cloudformation:*",
          "cloudwatch:*",
          "ec2:*",
          "ecr:*",
          "ecs:*",
          "elasticloadbalancing:*",
          "events:*",
          "iam:*",
          "kms:*",
          "logs:*",
          "route53:*",
          "s3:*",
          "secretsmanager:*",
          "servicequotas:*",
          "sns:*",
          "ssm:*",
          "sts:*",
          "tag:*",
          "wafv2:*"
        ],
        "Resource": "*"
      }
    ]
  }' \
  --profile "$AWS_BOOTSTRAP_PROFILE"
```

Confirm that IAM stored the policy:

```zsh
aws iam get-user-policy \
  --user-name "$MUTUALGPU_IAM_USER" \
  --policy-name "$MUTUALGPU_IAM_POLICY" \
  --profile "$AWS_BOOTSTRAP_PROFILE"
```

## 4. Create the console password

Read the temporary password without adding it to shell history, then create a login profile that forces a change at first sign-in:

```zsh
read -s "MUTUALGPU_TEMP_PASSWORD?Temporary console password: "
print

aws iam create-login-profile \
  --user-name "$MUTUALGPU_IAM_USER" \
  --password "$MUTUALGPU_TEMP_PASSWORD" \
  --password-reset-required \
  --profile "$AWS_BOOTSTRAP_PROFILE"

unset MUTUALGPU_TEMP_PASSWORD
```

The expanded password may briefly be visible to other processes on the same machine, so run this only from a trusted workstation. It is not stored in shell history.

Get the account-specific console URL:

```zsh
MUTUALGPU_ACCOUNT_ID="$(aws sts get-caller-identity \
  --query Account \
  --output text \
  --profile "$AWS_BOOTSTRAP_PROFILE")"

print "https://${MUTUALGPU_ACCOUNT_ID}.signin.aws.amazon.com/console/"
```

Sign in as the deployment user with the temporary password. AWS will require a replacement password immediately. AWS documents this flow under [setting an initial IAM password](https://docs.aws.amazon.com/cli/latest/userguide/cli-services-iam.html).

### Administrator password reset

If the password is lost, an administrator can replace it:

```zsh
read -s "MUTUALGPU_NEW_PASSWORD?New console password: "
print

aws iam update-login-profile \
  --user-name "$MUTUALGPU_IAM_USER" \
  --password "$MUTUALGPU_NEW_PASSWORD" \
  --password-reset-required \
  --profile "$AWS_BOOTSTRAP_PROFILE"

unset MUTUALGPU_NEW_PASSWORD
```

Use `--no-password-reset-required` only when the administrator intentionally sets the user's final password. The password cannot be recovered, only replaced; see [`update-login-profile`](https://docs.aws.amazon.com/cli/latest/reference/iam/update-login-profile.html).

### Change your own password through the CLI

After the access-key profile in the next section exists, the user can change its own console password:

```zsh
read -s "MUTUALGPU_OLD_PASSWORD?Current console password: "
print
read -s "MUTUALGPU_NEW_PASSWORD?New console password: "
print

aws iam change-password \
  --old-password "$MUTUALGPU_OLD_PASSWORD" \
  --new-password "$MUTUALGPU_NEW_PASSWORD" \
  --profile "$MUTUALGPU_CLI_PROFILE"

unset MUTUALGPU_OLD_PASSWORD MUTUALGPU_NEW_PASSWORD
```

The forced first-login change in the console is simpler; this CLI form is included for later password changes.

## 5. Create and configure access keys

Create one access key with the bootstrap profile only when rebuilding the user. If the administrator has already created the key and configured `ecs-fargate`, skip this command during remediation:

```zsh
aws iam create-access-key \
  --user-name "$MUTUALGPU_IAM_USER" \
  --profile "$AWS_BOOTSTRAP_PROFILE" \
  --output json
```

The response contains `AccessKeyId` and `SecretAccessKey`. AWS shows the secret only in this response. Copy both values directly into a password manager, then configure a named local profile:

```zsh
aws configure --profile "$MUTUALGPU_CLI_PROFILE"
```

Enter:

```text
AWS Access Key ID: <AccessKeyId from create-access-key>
AWS Secret Access Key: <SecretAccessKey from create-access-key>
Default region name: us-east-1
Default output format: json
```

Do not paste the key into this repository, a shell script, task definition, chat, issue, or CI log. The shared AWS credentials file is plaintext, so restrict access to the workstation that holds it.

Verify the new profile independently:

```zsh
aws sts get-caller-identity --profile "$MUTUALGPU_CLI_PROFILE"
aws iam get-user --user-name "$MUTUALGPU_IAM_USER" --profile "$MUTUALGPU_CLI_PROFILE"
aws ecs list-clusters --region "$AWS_REGION" --profile "$MUTUALGPU_CLI_PROFILE"
```

The returned account ID and ARN must identify the intended account and deployment user.

## 6. Enable MFA

The fastest path is to sign in to the console as the deployment user, open **Security credentials**, and assign a passkey, security key, or authenticator app.

To create a virtual authenticator entirely through the CLI, write its one-time QR bootstrap material outside the repository:

```zsh
export MUTUALGPU_MFA_QR=/tmp/mutualgpu-provisioning-mfa.png
MUTUALGPU_ACCOUNT_ID="${MUTUALGPU_ACCOUNT_ID:-$(aws sts get-caller-identity \
  --query Account \
  --output text \
  --profile "$MUTUALGPU_CLI_PROFILE")}"

aws iam create-virtual-mfa-device \
  --virtual-mfa-device-name "$MUTUALGPU_IAM_USER" \
  --bootstrap-method QRCodePNG \
  --outfile "$MUTUALGPU_MFA_QR" \
  --profile "$MUTUALGPU_CLI_PROFILE"
```

Scan that file with the authenticator, obtain two consecutive codes, then enable it:

```zsh
read "MUTUALGPU_MFA_CODE_1?First MFA code: "
read "MUTUALGPU_MFA_CODE_2?Next MFA code: "

aws iam enable-mfa-device \
  --user-name "$MUTUALGPU_IAM_USER" \
  --serial-number "arn:aws:iam::${MUTUALGPU_ACCOUNT_ID}:mfa/${MUTUALGPU_IAM_USER}" \
  --authentication-code-1 "$MUTUALGPU_MFA_CODE_1" \
  --authentication-code-2 "$MUTUALGPU_MFA_CODE_2" \
  --profile "$MUTUALGPU_CLI_PROFILE"

unset MUTUALGPU_MFA_CODE_1 MUTUALGPU_MFA_CODE_2
```

Permanently delete `/tmp/mutualgpu-provisioning-mfa.png` after the device is enabled because the QR code contains the authenticator seed. AWS documents the manual ordering in [assign MFA devices using the CLI](https://docs.aws.amazon.com/IAM/latest/UserGuide/id_credentials_mfa_enable_cliapi.html).

This broad policy recommends MFA but does not technically require MFA for every API call. Adding such enforcement would require using `sts:GetSessionToken` for CLI sessions and is intentionally outside the expedited setup.

## 7. Inspect, rotate, or disable keys

List the user's keys:

```zsh
aws iam list-access-keys \
  --user-name "$MUTUALGPU_IAM_USER" \
  --profile "$AWS_BOOTSTRAP_PROFILE"
```

For rotation, create a second key, configure and verify it, then deactivate the old key before deleting it:

```zsh
aws iam update-access-key \
  --user-name "$MUTUALGPU_IAM_USER" \
  --access-key-id <OLD_ACCESS_KEY_ID> \
  --status Inactive \
  --profile "$AWS_BOOTSTRAP_PROFILE"

aws iam delete-access-key \
  --user-name "$MUTUALGPU_IAM_USER" \
  --access-key-id <OLD_ACCESS_KEY_ID> \
  --profile "$AWS_BOOTSTRAP_PROFILE"
```

AWS permits at most two access keys per IAM user. Never delete the old key until the replacement profile has passed `sts get-caller-identity` and a representative provisioning command.

## 8. Disable provisioning access after the demo

The infrastructure runs through ECS task and execution roles; it does not need the human provisioning user's access keys after deployment.

At minimum, deactivate the key:

```zsh
aws iam update-access-key \
  --user-name "$MUTUALGPU_IAM_USER" \
  --access-key-id <ACCESS_KEY_ID> \
  --status Inactive \
  --profile "$AWS_BOOTSTRAP_PROFILE"
```

To remove console access as well:

```zsh
aws iam delete-login-profile \
  --user-name "$MUTUALGPU_IAM_USER" \
  --profile "$AWS_BOOTSTRAP_PROFILE"
```

Keep the user only if another deployment is imminent. Otherwise, remove every credential before deleting it. Substitute the IDs returned by the list commands; repeat the access-key commands if more than one key exists:

```zsh
MUTUALGPU_ACCOUNT_ID="${MUTUALGPU_ACCOUNT_ID:-$(aws sts get-caller-identity \
  --query Account \
  --output text \
  --profile "$AWS_BOOTSTRAP_PROFILE")}"

aws iam list-access-keys \
  --user-name "$MUTUALGPU_IAM_USER" \
  --profile "$AWS_BOOTSTRAP_PROFILE"

aws iam update-access-key \
  --user-name "$MUTUALGPU_IAM_USER" \
  --access-key-id <ACCESS_KEY_ID> \
  --status Inactive \
  --profile "$AWS_BOOTSTRAP_PROFILE"

aws iam delete-access-key \
  --user-name "$MUTUALGPU_IAM_USER" \
  --access-key-id <ACCESS_KEY_ID> \
  --profile "$AWS_BOOTSTRAP_PROFILE"

aws iam list-mfa-devices \
  --user-name "$MUTUALGPU_IAM_USER" \
  --profile "$AWS_BOOTSTRAP_PROFILE"

aws iam deactivate-mfa-device \
  --user-name "$MUTUALGPU_IAM_USER" \
  --serial-number "arn:aws:iam::${MUTUALGPU_ACCOUNT_ID}:mfa/${MUTUALGPU_IAM_USER}" \
  --profile "$AWS_BOOTSTRAP_PROFILE"

aws iam delete-virtual-mfa-device \
  --serial-number "arn:aws:iam::${MUTUALGPU_ACCOUNT_ID}:mfa/${MUTUALGPU_IAM_USER}" \
  --profile "$AWS_BOOTSTRAP_PROFILE"

aws iam delete-login-profile \
  --user-name "$MUTUALGPU_IAM_USER" \
  --profile "$AWS_BOOTSTRAP_PROFILE"

aws iam delete-user-policy \
  --user-name "$MUTUALGPU_IAM_USER" \
  --policy-name "$MUTUALGPU_IAM_POLICY" \
  --profile "$AWS_BOOTSTRAP_PROFILE"

aws iam delete-user \
  --user-name "$MUTUALGPU_IAM_USER" \
  --profile "$AWS_BOOTSTRAP_PROFILE"
```

IAM refuses to delete the user while login profiles, access keys, or MFA associations remain, which helps prevent accidental orphaning.

## Bootstrap completion checklist

- [ ] Bootstrap identity was verified against the intended AWS account.
- [ ] The deployment user exists and has the inline full-control policy.
- [ ] Temporary console password was changed on first sign-in.
- [ ] Exactly one access key was created and stored outside the repository.
- [ ] The `ecs-fargate` CLI profile passes `sts get-caller-identity`.
- [ ] MFA is enabled and the QR bootstrap file is destroyed.
- [ ] Account ID, deployment region, and `mutualgpu.com` ownership are confirmed.
- [ ] A reminder exists to deactivate or delete the access key after the demo.
