# MutualGPU source-account resource and teardown manifest

Observed: 2026-07-30 23:14:55 UTC  
Source profile: `ai-quinn`  
Verified source principal: `arn:aws:iam::351791602105:user/ai_quinn`  
Source account: `351791602105`  
Live region: `us-east-1`  
Manifest JSON: `docs/mutualgpu-source-resource-manifest.json`

TLS migration issues are recorded as already handled per user confirmation. The
source ACM certificate remains in this document only because it is still a
source-owned resource to remove after the source ALB is gone.

## Result

The current MutualGPU AWS footprint is:

- 2 live CloudFormation stacks with 26 stack-member resources;
- 4 confirmed standalone resources not owned by either stack;
- 53 ECS task-definition revisions, of which only revision 53 is the current
  stack resource;
- 21 observed service-managed or VPC-implicit child resources;
- 155 ECR image artifacts, 75 CloudWatch log streams, 11,089 current S3
  objects, 15,002 S3 object versions, and 4,435 S3 delete markers.

Every confirmed owned resource is in `us-east-1`, except for the two global IAM
roles and globally named S3 buckets. No other enabled region contained a
MutualGPU stack, `Project=MutualGPU` resource, or MutualGPU name/tag match.

This is a read-only snapshot, not teardown authorization. Running tasks, ENIs,
Elastic IPs, object counts, image counts, log streams, and other dynamic children
can change. Refresh the JSON manifest immediately before teardown.

## Ownership and coverage

Ownership is confirmed when a resource is a current stack member, a direct
reference from the live CloudFormation/ECS/IAM configuration, or has
MutualGPU-specific naming/tags corroborated by CloudTrail creation metadata.

The inventory used:

- STS identity verification;
- CloudFormation stack and stack-resource enumeration in all 18 enabled regions;
- Resource Groups Tagging API scans in all enabled regions;
- direct ECS, ECR, EC2, ELBv2, WAFv2, IAM, S3, Secrets Manager, ACM,
  CloudWatch Logs, CloudWatch, SSM, KMS, Route 53, and Application Auto Scaling
  reads;
- CloudTrail event metadata across the complete project creation window,
  2026-07-20 through 2026-07-30.

Four account-wide cross-checks were permission-limited:

- `rds:DescribeDBInstances`, `rds:DescribeDBClusters`, and
  `rds:DescribeDBSnapshots`;
- `route53domains:ListDomains`;
- Resource Explorer 2;
- AWS Config.

These gaps do not indicate a live MutualGPU RDS or Route 53 Domains resource:

- the live foundation stack has no RDS member;
- the active ECS task definition has the legacy structured-S3 configuration
  keys and no `MutualGPU__Postgres__*` keys;
- Secrets Manager has no `mutualgpu/postgres` secret;
- the tagging scan found no RDS match;
- CloudTrail contains no MutualGPU RDS creation in the project creation window;
- the source account has no MutualGPU Route 53 hosted zone or Route 53 mutation;
- public registry metadata identifies Spaceship, Inc. as registrar and
  `launch1.spaceship.net` / `launch2.spaceship.net` as nameservers.

The resulting conclusion is high-confidence: the live source is the legacy
structured-S3 deployment, not the PostgreSQL deployment described by the current
repository templates.

## Live orchestration

| Delete order | Stack | ARN | Status | Members | Blockers |
|---:|---|---|---|---:|---|
| 1 | `mutualgpu-service` | `arn:aws:cloudformation:us-east-1:351791602105:stack/mutualgpu-service/7aa6a0f0-84df-11f1-bd32-12db278e9a53` | `UPDATE_COMPLETE` | 10 | Complete cutover and copy the deployed image first |
| 2 | `mutualgpu-foundation` | `arn:aws:cloudformation:us-east-1:351791602105:stack/mutualgpu-foundation/0eb89e80-84c5-11f1-b2b6-0affca082aef` | `UPDATE_COMPLETE` | 16 | Service stack deleted; ECR emptied; out-of-band IAM attachments removed |

Termination protection is disabled on both stacks.

## `mutualgpu-service` members

| Logical ID | Type | Physical ID |
|---|---|---|
| `LoadBalancer` | `AWS::ElasticLoadBalancingV2::LoadBalancer` | `arn:aws:elasticloadbalancing:us-east-1:351791602105:loadbalancer/app/mutualgpu-demo/d7e74d63d20706c6` |
| `HttpListener` | `AWS::ElasticLoadBalancingV2::Listener` | `arn:aws:elasticloadbalancing:us-east-1:351791602105:listener/app/mutualgpu-demo/d7e74d63d20706c6/b68da4d597fc2a04` |
| `HttpsListener` | `AWS::ElasticLoadBalancingV2::Listener` | `arn:aws:elasticloadbalancing:us-east-1:351791602105:listener/app/mutualgpu-demo/d7e74d63d20706c6/bdc92c4395b039ea` |
| `GrpcRule` | `AWS::ElasticLoadBalancingV2::ListenerRule` | `arn:aws:elasticloadbalancing:us-east-1:351791602105:listener-rule/app/mutualgpu-demo/d7e74d63d20706c6/bdc92c4395b039ea/150fe947e63dc3c4` |
| `WebTargetGroup` | `AWS::ElasticLoadBalancingV2::TargetGroup` | `arn:aws:elasticloadbalancing:us-east-1:351791602105:targetgroup/mutualgpu-web/1b7272bd6ab1bf84` |
| `GrpcTargetGroup` | `AWS::ElasticLoadBalancingV2::TargetGroup` | `arn:aws:elasticloadbalancing:us-east-1:351791602105:targetgroup/mutualgpu-grpc/7016ea4bc8c06d7a` |
| `ProviderEnrollmentWebAcl` | `AWS::WAFv2::WebACL` | `mutualgpu-provider-enrollment\|418abe21-8d22-4319-9049-7d5ce1d2fdcb\|REGIONAL` |
| `ProviderEnrollmentWebAclAssociation` | `AWS::WAFv2::WebACLAssociation` | ALB/WebACL composite ID in the JSON manifest |
| `Service` | `AWS::ECS::Service` | `arn:aws:ecs:us-east-1:351791602105:service/mutualgpu-demo/mutualgpu-api` |
| `TaskDefinition` | `AWS::ECS::TaskDefinition` | `arn:aws:ecs:us-east-1:351791602105:task-definition/mutualgpu-api:53` |

The service is Fargate, desired/running count 1, using task definition revision
53 and image index:

```text
351791602105.dkr.ecr.us-east-1.amazonaws.com/mutualgpu-api@sha256:519b123233e0940c5f4b579e119ec8de3d4e168b6385af19b1c15d9f9d97ac31
```

## `mutualgpu-foundation` members

| Logical ID | Type | Physical ID |
|---|---|---|
| `MutualGpuVpc` | `AWS::EC2::VPC` | `vpc-0ddcf2b09606ad7b2` |
| `InternetGateway` | `AWS::EC2::InternetGateway` | `igw-0d488d309f1cf60e9` |
| `InternetGatewayAttachment` | `AWS::EC2::VPCGatewayAttachment` | `IGW\|vpc-0ddcf2b09606ad7b2` |
| `PublicSubnetA` | `AWS::EC2::Subnet` | `subnet-0d7a2a5a8c96696a6` |
| `PublicSubnetB` | `AWS::EC2::Subnet` | `subnet-054038fe2173403b5` |
| `PublicRouteTable` | `AWS::EC2::RouteTable` | `rtb-0e3bb1ccc115d0b38` |
| `DefaultPublicRoute` | `AWS::EC2::Route` | `rtb-0e3bb1ccc115d0b38\|0.0.0.0/0` |
| `PublicSubnetARouteTableAssociation` | `AWS::EC2::SubnetRouteTableAssociation` | `rtbassoc-0e8cd536b70b8368f` |
| `PublicSubnetBRouteTableAssociation` | `AWS::EC2::SubnetRouteTableAssociation` | `rtbassoc-033f6d0ed6be963e8` |
| `AlbSecurityGroup` | `AWS::EC2::SecurityGroup` | `sg-0c736c2cc9b48fd0a` |
| `ApiSecurityGroup` | `AWS::EC2::SecurityGroup` | `sg-08a92d481e9a267fe` |
| `ImageRepository` | `AWS::ECR::Repository` | `mutualgpu-api` |
| `Cluster` | `AWS::ECS::Cluster` | `mutualgpu-demo` |
| `ApiLogGroup` | `AWS::Logs::LogGroup` | `/ecs/mutualgpu-api` |
| `TaskExecutionRole` | `AWS::IAM::Role` | `mutualgpu-ecs-task-execution` |
| `TaskRole` | `AWS::IAM::Role` | `mutualgpu-ecs-task` |

The VPC is `10.42.0.0/16`, with public subnets `10.42.0.0/20` in
`us-east-1a` (`use1-az4`) and `10.42.16.0/20` in `us-east-1b`
(`use1-az6`). There is no NAT gateway, VPC endpoint, VPC peering connection,
transit-gateway attachment, EC2 instance, or VPC flow log.

## Standalone resources

These resources are confirmed MutualGPU-owned but are not stack members. They
require explicit cleanup.

| Service | Resource | State and teardown concern |
|---|---|---|
| S3 | `arn:aws:s3:::mutualgpu-data` | Versioning enabled; 10,418 current objects / 563,462,911 bytes; 14,331 versions / 570,339,282 bytes; 4,435 delete markers |
| S3 | `arn:aws:s3:::mutualgpu-preshared-keys` | Untagged but live-referenced; versioning disabled; 671 objects / 933,439 bytes, all null versions |
| Secrets Manager | `arn:aws:secretsmanager:us-east-1:351791602105:secret:mutualgpu/deployment-Adk6Uk` | 2 versions; no rotation or replication; never inspect or print secret values |
| ACM | `arn:aws:acm:us-east-1:351791602105:certificate/77805af9-10d0-4583-88f7-006a5f80dbf1` | Issued for `mutualgpu.com`; currently attached to the ALB |

Both buckets use `BucketOwnerEnforced`, SSE-S3 (`AES256`), and full public-access
blocking. Only `mutualgpu-data` has CORS. Neither bucket has incomplete multipart
uploads. The data bucket must be purged by version ID, including all delete
markers; deleting only current keys is insufficient.

## Residual ECS task definitions

There are 53 `mutualgpu-api` revisions. Revision 53 is the current service-stack
resource, leaving 52 historical revisions outside the current stack.

Active revisions (19):

```text
2, 5, 6, 7, 9, 10, 11, 12, 13, 21, 25, 42, 43, 44, 45, 46, 47, 48, 53
```

Inactive revisions (34):

```text
1, 3, 4, 8, 14, 15, 16, 17, 18, 19, 20, 22, 23, 24, 26, 27, 28, 29,
30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 49, 50, 51, 52
```

A service-stack deletion handles only the current stack relationship. After the
service is gone, deregister every remaining active revision and permanently
delete every inactive revision.

## ECR and log data

`mutualgpu-api` contains:

- 155 image artifacts;
- 49 tagged and 106 untagged artifacts;
- 9,607,136,746 total compressed bytes reported by ECR;
- no lifecycle policy;
- no repository policy;
- no `EmptyOnDelete` property in the live CloudFormation template.

The active digest is:

```text
sha256:519b123233e0940c5f4b579e119ec8de3d4e168b6385af19b1c15d9f9d97ac31
```

The repository must be copied as required by the migration and emptied before
deleting `mutualgpu-foundation`, or stack deletion will fail on the non-empty
repository.

`/ecs/mutualgpu-api` has 75 log streams and 145,327,831 stored bytes with
14-day retention. Stack deletion removes the group and streams. Export logs first
only if the retention plan requires them.

## IAM drift and deletion blocker

The stack owns:

- `mutualgpu-ecs-task-execution`, with inline
  `ReadMutualGpuDeploymentSecrets` and template-managed
  `AmazonECSTaskExecutionRolePolicy`;
- `mutualgpu-ecs-task`, with inline `AccessMutualGpuStorage`.

Both roles also have `AmazonS3FullAccess` attached out of band by the account root
on 2026-07-21. Those AWS-managed policies are shared policies, not
MutualGPU-owned resources, but the two attachments are part of the current
MutualGPU role state and can block role deletion. Detach both out-of-band
attachments before deleting the foundation stack.

The inline task policy confirms the exact buckets:

```text
arn:aws:s3:::mutualgpu-data
arn:aws:s3:::mutualgpu-preshared-keys
```

## Service-managed and implicit children

Do not manually delete these before their parent. They should disappear with the
service, load balancer, security group, or VPC:

- VPC CIDR association `vpc-cidr-assoc-0c7f1b8d790d0c081`;
- default security group `sg-0baf3ea54b066df8f`;
- main route table `rtb-073e469d7279a02b0` and association
  `rtbassoc-03c9b0b4d6e356315`;
- default network ACL `acl-021ccbad4d27c44f8` and associations
  `aclassoc-0c2c0360199423c23` / `aclassoc-0e3cd46809c75e3c9`;
- six security-group rule IDs listed in the JSON manifest;
- current ECS task
  `arn:aws:ecs:us-east-1:351791602105:task/mutualgpu-demo/f64faeef7f3a4a37819190004efd51e8`
  and ENI `eni-06468a92eb5728f47`;
- ALB ENIs `eni-06df60a22e56563d8` and `eni-0d2f2d7506fe52063`;
- ALB-managed Elastic IP allocations `eipalloc-03f72ebaf9dac3458` and
  `eipalloc-0c1667e6bda047dfd`;
- HTTP and HTTPS default listener rules listed in the JSON manifest.

The ECS task and all network addresses are dynamic observations and may rotate.

## Shared and external dependencies to exclude

Never delete these as MutualGPU resources:

- AWS-managed KMS key `alias/aws/acm`
  (`74407914-00b3-46d0-ac02-945bf4eb7c3c`);
- account-scoped `AWSServiceRoleForECS`;
- account-scoped `AWSServiceRoleForElasticLoadBalancing`;
- `mutualgpu.com` registration and DNS, which are managed through Spaceship,
  not this AWS account.

The domain and DNS remain migration dependencies. Their external ownership does
not authorize changing or deleting them.

## Negative findings

No live MutualGPU resource was found in:

- RDS, DynamoDB, Lambda, API Gateway, EventBridge, SQS, SNS, CloudFront, EFS,
  ElastiCache, or OpenSearch;
- Route 53 hosted zones;
- SSM Parameter Store or customer-managed KMS aliases;
- CloudWatch alarms or dashboards;
- ECS Application Auto Scaling;
- NAT gateways, VPC endpoints, peering connections, EC2 instances, EBS volumes,
  or launch templates.

CloudTrail creation activity for `ai_quinn` during the project window is limited
to ACM, CloudFormation, EC2 networking, ECR, ECS, ELBv2, IAM, CloudWatch Logs,
S3, Secrets Manager, and WAFv2. One failed/duplicate first service stack and its
ALB/target groups/listeners/service were already deleted on 2026-07-21; their
old physical IDs are historical and not teardown targets.

## Teardown sequence

1. Complete target cutover, source write freeze, S3/provider-state migration,
   image copy, rollback-retention approval, and the remaining non-TLS migration
   gates.
2. Delete `mutualgpu-service`.
3. Wait for the ECS task, ALB ENIs, ALB-managed Elastic IPs, and default listener
   rules to disappear.
4. Deregister the remaining active `mutualgpu-api` task definitions and
   permanently delete all inactive revisions.
5. Detach the out-of-band `AmazonS3FullAccess` policy from both stack-owned IAM
   roles.
6. Empty the 155 ECR artifacts after the image copy is verified.
7. Delete `mutualgpu-foundation`, then verify its 16 members and all implicit VPC
   children are gone.
8. Delete the now-unused ACM certificate.
9. Schedule deletion of `mutualgpu/deployment` using the reviewed recovery
   window.
10. After the approved source-retention period, purge all versions and delete
    markers from `mutualgpu-data`, empty `mutualgpu-preshared-keys`, and delete
    both buckets.
11. Repeat the regional/global name, tag, dependency, and billing checks. Only
    the excluded shared dependencies and external DNS should remain.

Do not use forced secret deletion, shorten the source-retention period, or delete
any source data without separate explicit authorization.
