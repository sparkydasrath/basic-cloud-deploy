# Deploying .NET apps to AWS — the production way

**For a .NET developer who already knows the manual CLI path.** This takes you from "I can `aws`
my way to a running app" to "I ship the way a real team ships." It is a learning ladder: each rung
is a thing you build, in order, and each rung is useful on its own. Nothing here is tied to a
specific app — it is the industry-standard way to put an ASP.NET Core service into production on
AWS.

---

## 1. The mental shift (this is the whole point)

Manual CLI deploy teaches you the *pieces*. Production teaches you three habits on top:

| Manual CLI you learned | Production adds | Why it matters |
|---|---|---|
| You type commands to make a thing exist | **The thing is described in code (IaC)** and applied | Repeatable, reviewable, version-controlled. Delete the whole stack and rebuild it identically. |
| You deploy from your laptop | **A pipeline deploys** on a git push | No laptop-only credentials; an audit trail; anyone on the team can ship. |
| You watch it and hope | **Health checks, rolling deploys, rollback, alarms** | A bad build does not take the site down. You find out before your users do. |

Everything below serves those three: **describe it in code, ship it from a pipeline, make it safe
to fail.**

A one-line test for "is this production": *if my laptop fell in a lake, could the team still
deploy?* If yes, you did it right.

---

## 2. The target picture (what you are building toward)

For a typical .NET web API, the standard AWS production shape:

```
        Internet
           |
        [Route 53]  DNS
           |
        [ACM cert]  TLS terminated here
           |
   [Application Load Balancer]
           |  health checks /health, /ready
     +-----+-----+
     |           |
 [Fargate task] [Fargate task]   <- your dotnet container, 2+ copies
     |           |
     +-----+-----+
           |
        [RDS Postgres/SQL Server]   <- managed, backed up
        [S3]                        <- object storage
        [Secrets Manager / SSM]     <- connection strings, keys
           |
        [CloudWatch + OTel]  <- logs, metrics, traces
```

The standard building blocks and what each replaces from a laptop/VM world:

| The old way | AWS managed building block |
|---|---|
| A reverse proxy (nginx/IIS) terminating TLS | **ALB + ACM** (Application Load Balancer + AWS Certificate Manager) |
| App running on a VM / IIS | **ECS Fargate** service (serverless containers) |
| A database on a box | **RDS** (PostgreSQL / SQL Server) or **Aurora Serverless v2** |
| A file share / blob store | **S3** |
| `appsettings.json` secrets / `.env` | **AWS Secrets Manager** + **SSM Parameter Store** |
| Log files on disk | **CloudWatch Logs** + **AWS X-Ray** (or an OTel backend) |
| A deploy script / clicking the console | **AWS CDK** (infrastructure as C# code) |

The twelve-factor discipline that makes an app easy to containerize is exactly what makes it easy
to ship: stateless process, config from the environment, logs to stdout, backing services attached
by URL. If your app already follows those, AWS is just running the managed versions.

---

## 3. Pick your compute (decide once, then stop re-deciding)

This guide is built on **ECS Fargate**. Here is why, and when you would not.

| Option | What it is | Pick it when | Skip it when |
|---|---|---|---|
| **ECS Fargate** ✅ | Run containers, no servers to manage | You have a container and want it running reliably with the least ops. The default for most .NET web APIs. | You need Kubernetes-specific tooling. |
| App Runner | Even simpler: point it at an image, get a URL | You want the fastest path and don't need fine network control | You need custom VPC networking, sidecars, or fine-grained scaling. |
| Elastic Beanstalk | Older PaaS, auto-provisions EC2 | Legacy shops; you inherited it | Greenfield — it hides things you should learn. |
| EKS (Kubernetes) | Managed Kubernetes | The org is already on k8s, or you run many services with complex networking | A single API — huge overhead for the value. |
| Lambda | Functions, per-request billing | Event-driven, spiky, or low traffic; a small API | Long-running work, cold-start sensitivity, or a stateful connection pool. |

**For learning "how a .NET dev ships to production," Fargate is the highest-transfer skill.** It
teaches containers, IaC, load balancing, networking, secrets, and CI/CD — the pieces that carry
over to every other option. Learn it first; the rest are variations on it.

---

## 4. The build ladder (do these in order)

Each rung ends in something that works. Do not skip ahead — each depends on the one before.

### Rung 0 — Containerize like production, not like a demo

Production containers add discipline over "it runs in Docker":

- **Multi-stage build** — build with the SDK image, run on the tiny runtime image. Smaller image
  = faster deploys, smaller attack surface.
- **Run as non-root.** A container that runs as root is a security finding.
- **A real health endpoint** the load balancer can call — expose `/health` (liveness) and
  `/ready` (readiness) with ASP.NET Core health checks.
- **Config from environment**, never baked into the image. The same image runs in dev, staging,
  and prod; only the environment differs.

A production-shaped `Dockerfile` for an ASP.NET Core API:

```dockerfile
# ---- build stage ----
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY *.sln .
COPY src/MyApi/*.csproj src/MyApi/
RUN dotnet restore src/MyApi/MyApi.csproj
COPY . .
RUN dotnet publish src/MyApi/MyApi.csproj -c Release -o /app --no-restore

# ---- runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app .
# the aspnet image ships a non-root user 'app' (uid 64198)
USER app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "MyApi.dll"]
```

**Why the layer order:** copy the `.csproj` and restore *before* copying the rest. Docker caches
layers; your source changes far more often than your dependencies, so this makes rebuilds fast.

**Do not terminate TLS in the app.** On AWS the ALB terminates TLS and forwards plain HTTP to the
container on 8080. Do not use `UseHttpsRedirection()` inside the container — configure
`ForwardedHeaders` so the app trusts the ALB's `X-Forwarded-Proto`/`X-Forwarded-For` instead. Same
model as running behind nginx or IIS ARR.

Add the health checks in `Program.cs`:

```csharp
builder.Services.AddHealthChecks()
    .AddNpgSql(connectionString)          // or your DB check
    .AddCheck("self", () => HealthCheckResult.Healthy());

app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = _ => false                // liveness: is the process up?
});
app.MapHealthChecks("/ready");            // readiness: are dependencies up?
```

Verify locally: `docker build -t myapi .`, then `docker run -p 8080:8080 myapi`, then
`curl localhost:8080/health`.

### Rung 1 — ECR: the image registry

ECR (Elastic Container Registry) is where your built images live so Fargate can pull them.

```bash
# create the repo (once, and later in IaC)
aws ecr create-repository --repository-name myapi

# authenticate docker to your registry
aws ecr get-login-password --region us-east-1 \
  | docker login --username AWS --password-stdin <acct>.dkr.ecr.us-east-1.amazonaws.com

# tag with a VERSION — use the git SHA, never only 'latest'
docker tag myapi:latest <acct>.dkr.ecr.us-east-1.amazonaws.com/myapi:$(git rev-parse --short HEAD)
docker push <acct>.dkr.ecr.us-east-1.amazonaws.com/myapi:<sha>
```

**Why tag with the git SHA:** `latest` is a lie in production. When something breaks you must know
*exactly* which build is running and roll back to a *specific* prior one. An immutable SHA tag
gives you that. Turn on **tag immutability** and **scan-on-push** in the repo settings — free
vulnerability scanning of every image.

### Rung 2 — Infrastructure as Code (this is the biggest leap)

Stop clicking the console. Stop typing `aws ec2 create-...`. Describe the whole system in code and
apply it. This is the single habit that separates hobby from professional.

> **Follow-along:** the summary below gets you the idea. When you are ready to actually build it,
> go to **[Appendix A — Full CDK walkthrough](#appendix-a--full-cdk-walkthrough-follow-along)** at
> the end of this doc. It builds the same stack from an empty folder, one piece at a time, with
> every command and every error you will hit.

**Use AWS CDK in C#.** You write infrastructure in the language you already know, with types and
IntelliSense. CDK compiles your C# to CloudFormation and deploys it.

```bash
npm install -g aws-cdk        # the CDK CLI (a node tool; your stack is still C#)
mkdir infra && cd infra
cdk init app --language csharp
cdk bootstrap                 # one-time per account/region: sets up CDK's own resources
```

A first stack that stands up a Fargate service behind an ALB — ~30 lines replacing dozens of
console clicks:

```csharp
using Amazon.CDK;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.ECS;
using Amazon.CDK.AWS.ECS.Patterns;
using Amazon.CDK.AWS.ECR;

public class MyApiStack : Stack
{
    public MyApiStack(Construct scope, string id, IStackProps props = null)
        : base(scope, id, props)
    {
        var vpc = new Vpc(this, "Vpc", new VpcProps { MaxAzs = 2 });
        var cluster = new Cluster(this, "Cluster", new ClusterProps { Vpc = vpc });
        var repo = Repository.FromRepositoryName(this, "Repo", "myapi");

        // This L3 "pattern" wires ALB + target group + health check + service for you.
        var service = new ApplicationLoadBalancedFargateService(this, "Api",
            new ApplicationLoadBalancedFargateServiceProps
            {
                Cluster = cluster,
                DesiredCount = 2,                 // two copies = zero-downtime deploys
                Cpu = 512,
                MemoryLimitMiB = 1024,
                TaskImageOptions = new ApplicationLoadBalancedTaskImageOptions
                {
                    Image = ContainerImage.FromEcrRepository(repo, "latest"),
                    ContainerPort = 8080,
                    Environment = new Dictionary<string, string>
                    {
                        ["ASPNETCORE_ENVIRONMENT"] = "Production",
                    },
                },
                PublicLoadBalancer = true,
                CircuitBreaker = new DeploymentCircuitBreaker { Rollback = true },
            });

        service.TargetGroup.ConfigureHealthCheck(
            new Amazon.CDK.AWS.ElasticLoadBalancingV2.HealthCheck
            {
                Path = "/health",
                HealthyHttpCodes = "200",
            });
    }
}
```

Deploy: `cdk deploy`. Tear it all down: `cdk destroy`. **That** is the superpower — the entire
environment is disposable and reproducible.

**Terraform is the other industry default.** If a job posting says Terraform, learn it after CDK —
the concepts (declare desired state, `plan`, `apply`) are identical; only the syntax (HCL) differs.
CDK-in-C# is the faster on-ramp *for you* because it is C#.

**Read `cdk diff` before every `cdk deploy`.** It shows exactly what will change — `git diff` for
your infrastructure. Never apply a change you have not read.

### Rung 3 — Networking (the part everyone gets wrong first)

The ALB pattern above created a VPC for you. Understand what it made, because networking is where
production deploys fail silently.

- **VPC** — your private network in AWS.
- **Public subnets** — where the ALB lives (reachable from the internet).
- **Private subnets** — where your Fargate tasks and database live (**not** internet-reachable;
  only the ALB reaches the tasks, only the tasks reach the DB).
- **Security groups** — stateful firewalls. The rule you want: ALB → tasks on 8080, tasks → DB on
  5432/1433, and *nothing else inbound*. The database must never be publicly reachable.

**The mental model:** the load balancer is the only door to the outside. Everything valuable sits
behind it in private subnets.

### Rung 4 — Data and secrets

**RDS** replaces a database on a box. You get automated backups, patching, and point-in-time
restore. Put it in the private subnets. `db.t4g.micro` is cheap for learning; **Aurora Serverless
v2** scales to near-zero when idle.

**Secrets Manager** holds the connection string and any API keys. RDS can *generate* the password
and store it in Secrets Manager automatically — the password never appears in your code or your
CDK. The Fargate task reads it at start-up through its IAM role.

```csharp
var db = new DatabaseInstance(this, "Db", new DatabaseInstanceProps
{
    Engine = DatabaseInstanceEngine.Postgres(new PostgresInstanceEngineProps
        { Version = PostgresEngineVersion.VER_16 }),
    Vpc = vpc,
    VpcSubnets = new SubnetSelection { SubnetType = SubnetType.PRIVATE_ISOLATED },
    Credentials = Credentials.FromGeneratedSecret("appuser"),
});
db.Secret.GrantRead(service.TaskDefinition.TaskRole);
```

**The production secrets rule:** secrets live in Secrets Manager, the app reads them through an IAM
role, and no human ever sees the value. That is stronger than an `appsettings.Production.json` or a
`.env` file — no secret to leak in a commit. **Parameter Store (SSM)** is for *non-secret* config
(feature flags, log levels). Secrets → Secrets Manager; the rest → Parameter Store.

**Database migrations** are the genuinely hard part of container deploys (EF Core migrations, DbUp,
Flyway — same problem). Two production-safe patterns:
1. **A one-off ECS task** that runs the migrations, invoked by the pipeline *before* the new
   service version goes live.
2. **Migrate on start-up** with a distributed lock so only one task runs it. Simpler, riskier with
   many tasks — the lock is essential.

Rule: **migrations run once per deploy, before traffic shifts, and must be backward-compatible**
(the old app version must survive the new schema during the seconds both run in a rolling deploy).
This is the **expand/contract** pattern — worth a dedicated study session. It is the number-one
cause of broken container deploys.

### Rung 5 — CI/CD (ship from a pipeline, not your laptop)

**GitHub Actions with OIDC.** The current production standard, and it removes the worst security
mistake in AWS: long-lived access keys.

**OIDC (OpenID Connect)** lets GitHub Actions assume an AWS IAM role *without any stored AWS keys*.
GitHub proves its identity to AWS with a short-lived token; AWS hands back temporary credentials
scoped to exactly what the pipeline may do. Nothing to leak. (Azure DevOps and GitLab CI have the
same OIDC-to-AWS mechanism if that is your CI.)

```yaml
name: deploy
on:
  push:
    branches: [main]

permissions:
  id-token: write     # required for OIDC
  contents: read

jobs:
  deploy:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - name: Configure AWS credentials (OIDC, no stored keys)
        uses: aws-actions/configure-aws-credentials@v4
        with:
          role-to-assume: arn:aws:iam::<acct>:role/github-actions-deploy
          aws-region: us-east-1

      - name: Test
        run: dotnet test

      - name: Build, tag with SHA, push to ECR
        run: |
          aws ecr get-login-password | docker login --username AWS --password-stdin <acct>.dkr.ecr.us-east-1.amazonaws.com
          IMAGE=<acct>.dkr.ecr.us-east-1.amazonaws.com/myapi:${{ github.sha }}
          docker build -t $IMAGE .
          docker push $IMAGE

      - name: Deploy infra + new image
        run: cd infra && cdk deploy --require-approval never
```

**The habit this builds:** a push to `main` runs your tests, builds a versioned image, and rolls it
out — with a full log of who deployed what and when. If tests fail, nothing ships. Your test suite
becomes a *deploy gate*, not just a local check.

### Rung 6 — Safe deploys (make failure boring)

A production deploy must not be able to take the site down. Fargate gives you this if you set it up:

- **Rolling deploy (default):** ECS starts new-version tasks, waits for them to pass the ALB health
  check, shifts traffic, *then* stops the old tasks. If the new tasks never go healthy, the old ones
  keep serving. A broken build simply does not go live.
- **Circuit breaker (`Rollback = true`, set above):** if the new version fails to stabilize, ECS
  automatically rolls back to the last good one.
- **Blue/green (CodeDeploy):** the advanced version — stand up a full parallel copy, test it, flip
  traffic in one move, keep the old copy warm for instant rollback. Learn rolling first; reach for
  blue/green when a bad deploy is genuinely expensive.

**The mindset:** you should be *bored* by deploys. A deploy that makes you hold your breath is not
production — it is a manual deploy wearing a costume.

### Rung 7 — Observability (you cannot fix what you cannot see)

- **CloudWatch Logs** — Fargate ships the container's stdout here automatically. Use **structured
  (JSON) logging** (Serilog with a compact JSON formatter) so you can query it, not grep it.
- **CloudWatch Alarms** — alarm on 5xx rate, unhealthy target count, CPU, and latency. "Unhealthy
  targets > 0" tells you before users complain.
- **Tracing** — emit **OpenTelemetry** from ASP.NET Core and point the OTLP exporter at **AWS
  X-Ray** or a hosted OTel backend. A slow request then shows you *which* span is slow.
- **The dashboard** — one screen: request rate, error rate, p99 latency, cost.

**One discipline to build early:** know what your request logging captures (path, query string,
headers) *before* logs leave the box — so you never accidentally log a token, a password reset
link, or personal data. That is a habit, not a feature.

### Rung 8 — Cost control and teardown

- **Set a billing alarm on day one** — a hard budget with an email alert, before anything else runs.
- **`cdk destroy` when you stop for the day.** IaC's best gift for a learner: the whole stack is
  disposable. Rebuild it in minutes tomorrow. Nothing runs up a bill overnight.
- **Cheapest learning shapes:** Fargate Spot for tasks, `t4g.micro` RDS or Aurora Serverless v2,
  and destroy nightly.
- **The biggest surprise bill for beginners is the NAT gateway** (~$32/mo just to exist, plus data).
  For a learning stack, tear down nightly, or use a NAT-free design with **VPC endpoints** for
  ECR / Secrets Manager / CloudWatch.

---

## 5. A concrete hands-on sequence

Do these as weekend-sized sessions, in order. Each ends in something working. Use any small
ASP.NET Core API as the lab — a weather API, a URL shortener, a todo service.

1. **Containerize the API production-style** — the multi-stage Dockerfile above, non-root, run it
   locally, hit `/health`. *(No AWS yet.)*
2. **Push it to ECR by hand** — do it manually once so you understand what the pipeline will
   automate. Tag with the git SHA.
3. **Stand up Fargate + ALB in CDK** — `cdk deploy`, curl the ALB's DNS name at `/health`,
   `cdk destroy`. Feel the disposability.
4. **Add RDS + Secrets Manager** — wire the connection string through the task role. Run your EF
   Core / DbUp migrations as a one-off ECS task. Confirm the app talks to the managed DB.
5. **Lock down the networking** — tasks and DB in private subnets, ALB in public, security groups
   at the minimum. Prove the DB is *not* reachable from the internet.
6. **Build the GitHub Actions pipeline with OIDC** — push to `main` → tests → image → `cdk deploy`.
   Delete any AWS access keys you were using; prove you can ship without them.
7. **Turn on safe-deploy features** — circuit breaker + rollback. Deliberately push a broken build
   and watch it *not* go live and roll back. This is the lesson that sticks.
8. **Add observability** — structured logs to CloudWatch, a 5xx alarm, traces to X-Ray, one
   dashboard.

After rung 7 you have done, hands-on, what most "deployed to AWS" resumes only claim.

---

## 6. The production checklist (what "production-grade" actually means)

A deploy is production-grade when every box is true:

- [ ] The whole environment is described in code (IaC), not console clicks.
- [ ] `cdk diff` / `terraform plan` is reviewed before every apply.
- [ ] Images are versioned by git SHA, never only `latest`; the registry scans on push.
- [ ] The app is stateless; all config comes from environment / Secrets Manager.
- [ ] Secrets live in Secrets Manager and reach the app through an IAM role — no keys in code,
      commits, or CI config.
- [ ] The database is in a private subnet, not publicly reachable, with automated backups.
- [ ] Migrations run once per deploy, before traffic shifts, and are backward-compatible.
- [ ] Deploys ship from a pipeline on git push, gated by the test suite — not from a laptop.
- [ ] CI authenticates to AWS with OIDC, holding no long-lived credentials.
- [ ] Rolling deploys with health checks; a failed build cannot take the site down; rollback is
      automatic.
- [ ] Logs, metrics, and traces flow to a central place; alarms fire before users notice.
- [ ] There is a billing alarm and a known teardown path.
- [ ] IAM roles follow least privilege — each thing can do only what it needs.

---

## 7. Where to go deeper (primary sources)

Prefer AWS's own docs — they are current and the console links to them:

- **AWS CDK developer guide** (C# examples) — your IaC on-ramp.
- **Amazon ECS + Fargate developer guide** — task definitions, services, deployments.
- **AWS Deploy Tool for .NET CLI** (`dotnet aws deploy`) — a good *scaffolder*: let it generate a
  CDK stack, read what it produced, then own that CDK yourself.
- **AWS Well-Architected Framework** — the six pillars. Read the **security** and **reliability**
  pillars; they are the "why" behind this whole checklist.
- **GitHub Actions OIDC with AWS** — the `configure-aws-credentials` action's docs.

**The honest takeaway:** the manual CLI taught you the nouns. Production is three verbs on top —
*describe it in code, ship it from a pipeline, make it safe to fail.* Everything here is one of
those three. Build the eight rungs once and you will have shipped the way real .NET teams ship —
with a working stack to point at.

---

## Appendix A — Full CDK walkthrough (follow along)

This is Rung 2, done slowly. You start from an empty folder and end with an ASP.NET Core API
running on Fargate behind a load balancer, reachable on a public URL — all described in C#. Every
command is here. Do it once and IaC stops being abstract.

**What you will build:** a VPC, an ECS cluster, and a Fargate service behind an Application Load
Balancer, pulling an image from ECR, with a health check and automatic rollback. Roughly the same
30 lines from Rung 2, but built up piece by piece so you understand each line.

**Time:** about 90 minutes the first time, most of it waiting for AWS to create things.

**Cost warning:** this stack costs a few dollars a day mostly from the ALB and the NAT gateway.
**Run `cdk destroy` when you finish** (last step). Set a billing alarm first.

### A.0 — Prerequisites (get these green before you start)

You need four things installed and one thing configured:

```bash
# 1. .NET SDK (you have this)
dotnet --version            # expect 8.x or 9.x

# 2. Node.js — the CDK CLI is a node tool, even though your stack is C#
node --version              # expect 18+ ; install from nodejs.org if missing

# 3. The AWS CDK CLI
npm install -g aws-cdk
cdk --version               # expect 2.x

# 4. Docker (to build the image you will deploy)
docker --version

# 5. AWS credentials configured (you did this for the manual CLI path)
aws sts get-caller-identity # must print your account id, not an error
```

If `aws sts get-caller-identity` fails, fix that first — CDK uses the same credentials the CLI
uses. For learning, an IAM user with `AdministratorAccess` is fine; a real job uses a scoped role.

### A.1 — Bootstrap the account (one time, ever)

CDK needs a small set of its own resources in your account (an S3 bucket for assets, some IAM
roles). This is called **bootstrapping** and you do it once per account+region.

```bash
# replace with your real account id and region
cdk bootstrap aws://123456789012/us-east-1
```

You will see it create a CloudFormation stack called `CDKToolkit`. That is normal. If you switch
regions later, bootstrap the new region too. **Common error:** "Unable to resolve AWS account" means
your credentials are not set — go back to A.0 step 5.

### A.2 — Create the CDK project

```bash
mkdir myapi-infra && cd myapi-infra
cdk init app --language csharp
```

This scaffolds a normal C# solution. Look at what it made:

```
myapi-infra/
├── cdk.json                 # tells the CDK CLI how to run your app
├── src/
│   └── MyapiInfra/
│       ├── MyapiInfra.csproj
│       ├── Program.cs        # the ENTRY POINT — creates the app and your stacks
│       └── MyapiInfraStack.cs  # YOUR STACK — this is where you describe resources
└── ...
```

Two files matter:
- **`Program.cs`** — the entry point. It creates a CDK `App` and instantiates your stack(s).
- **`MyapiInfraStack.cs`** — the stack. Everything you add here becomes real AWS resources.

Build it once to confirm the scaffold compiles:

```bash
dotnet build src
```

### A.3 — Add the AWS construct libraries

The scaffold references the core CDK library. You need the service-specific constructs (EC2, ECS,
ECR). Add them:

```bash
cd src/MyapiInfra
dotnet add package Amazon.CDK.Lib
# Amazon.CDK.Lib is the "all AWS services in one package" library for CDK v2.
# You do NOT add per-service packages in v2 — they all live in Amazon.CDK.Lib.
cd ../..
```

If the scaffold already added `Amazon.CDK.Lib`, `dotnet add package` just confirms it. In CDK v2
every AWS service (EC2, ECS, RDS, S3, …) is a namespace *inside* `Amazon.CDK.Lib` — you never chase
separate NuGet packages per service. That is the big v1→v2 change; ignore any tutorial that adds
`Amazon.CDK.AWS.ECS` as its own package (that is v1).

### A.4 — Understand the entry point (`Program.cs`)

Open `Program.cs`. It looks about like this:

```csharp
using Amazon.CDK;

var app = new App();
new MyapiInfraStack(app, "MyapiInfraStack", new StackProps
{
    // pin the account + region so the stack is not "environment-agnostic"
    Env = new Amazon.CDK.Environment
    {
        Account = System.Environment.GetEnvironmentVariable("CDK_DEFAULT_ACCOUNT"),
        Region  = System.Environment.GetEnvironmentVariable("CDK_DEFAULT_REGION"),
    }
});
app.Synth();
```

What each line means:
- `new App()` — the root of everything. One app can hold many stacks.
- `new MyapiInfraStack(app, "MyapiInfraStack", …)` — creates *your* stack and gives it a name. That
  name becomes the CloudFormation stack name in the console.
- `Env` — pins which account and region this stack deploys to. `CDK_DEFAULT_ACCOUNT` /
  `CDK_DEFAULT_REGION` are filled from your current AWS credentials, so this "just works" for
  learning. In a real setup you name the account/region explicitly per environment (dev/prod).
- `app.Synth()` — **synthesize**: turn your C# into a CloudFormation template. This is what `cdk`
  actually deploys.

You will not change `Program.cs` much. The work happens in the stack.

### A.5 — Build the stack, one resource at a time

Now open `MyapiInfraStack.cs` and replace its body. We add resources in dependency order — network
first, then the cluster, then the service. Read each block's comment before you paste the next.

**Step 1 — the imports and the class shell.**

```csharp
using Amazon.CDK;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.ECS;
using Amazon.CDK.AWS.ECS.Patterns;
using Amazon.CDK.AWS.ECR;
using Amazon.CDK.AWS.ElasticLoadBalancingV2;
using Constructs;

namespace MyapiInfra;

public class MyapiInfraStack : Stack
{
    internal MyapiInfraStack(Construct scope, string id, IStackProps props = null)
        : base(scope, id, props)
    {
        // resources go here, in the steps below
    }
}
```

**Step 2 — the network (VPC).** Every container needs a network to live in.

```csharp
        // A VPC spread across 2 Availability Zones. CDK creates public + private
        // subnets, an internet gateway, and (by default) a NAT gateway automatically.
        var vpc = new Vpc(this, "Vpc", new VpcProps
        {
            MaxAzs = 2,          // 2 zones = high availability without over-paying
            NatGateways = 1,     // 1 NAT (not 2) keeps the learning bill down
        });
```

> **The NAT gateway is your biggest cost here (~$32/mo).** `NatGateways = 1` instead of the default
> 2 roughly halves it. For a pure learning stack you can set it to `0` and add VPC endpoints, but
> keep 1 for now — it is simpler and you will `destroy` nightly anyway.

**Step 3 — the ECS cluster.** A cluster is just a logical home for your services.

```csharp
        var cluster = new Cluster(this, "Cluster", new ClusterProps
        {
            Vpc = vpc,
        });
```

**Step 4 — reference the ECR repository.** You built and pushed an image in Rung 1. Point at that
repo (this does not create it — you created it with `aws ecr create-repository`; A.7 covers the
image).

```csharp
        var repo = Repository.FromRepositoryName(this, "Repo", "myapi");
```

**Step 5 — the Fargate service behind a load balancer.** This one construct does a *lot*: it
creates the task definition, the service, an Application Load Balancer, a target group, a listener,
and all the security groups wiring them together. It is called an **L3 (pattern) construct** —
opinionated, batteries included.

```csharp
        var service = new ApplicationLoadBalancedFargateService(this, "Api",
            new ApplicationLoadBalancedFargateServiceProps
            {
                Cluster = cluster,
                DesiredCount = 2,            // 2 tasks = zero-downtime rolling deploys
                Cpu = 512,                   // 0.5 vCPU
                MemoryLimitMiB = 1024,       // 1 GB
                PublicLoadBalancer = true,   // the ALB gets a public DNS name

                // if the new version won't go healthy, roll back automatically
                CircuitBreaker = new DeploymentCircuitBreaker { Rollback = true },

                TaskImageOptions = new ApplicationLoadBalancedTaskImageOptions
                {
                    // pull the image you pushed to ECR. Use a real tag in CI; 'latest'
                    // is fine while you are learning this one construct.
                    Image = ContainerImage.FromEcrRepository(repo, "latest"),
                    ContainerPort = 8080,    // must match EXPOSE / ASPNETCORE_URLS in your Dockerfile
                    Environment = new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["ASPNETCORE_ENVIRONMENT"] = "Production",
                    },
                },
            });
```

**Step 6 — point the health check at your real endpoint.** By default the ALB pings `/` and
expects 200. Your app answers on `/health`, so tell it:

```csharp
        service.TargetGroup.ConfigureHealthCheck(new Amazon.CDK.AWS.ElasticLoadBalancingV2.HealthCheck
        {
            Path = "/health",
            HealthyHttpCodes = "200",
            Interval = Duration.Seconds(30),
        });
```

**Step 7 — output the URL so you don't have to hunt for it.** After deploy, CDK prints this.

```csharp
        new CfnOutput(this, "ApiUrl", new CfnOutputProps
        {
            Value = $"http://{service.LoadBalancer.LoadBalancerDnsName}",
            Description = "Public URL of the API",
        });
```

That is the whole stack. Build it to catch typos before you deploy:

```bash
dotnet build src
```

### A.6 — See what CDK will create, WITHOUT creating it

Two commands you will run constantly. Neither touches AWS beyond reading.

```bash
# synthesize: turn your C# into the CloudFormation template and print it
cdk synth
```

`cdk synth` prints ~hundreds of lines of YAML. **You do not read all of it** — you skim to confirm
the resources you expect are there (a VPC, an ECS service, an ALB). The point is: your 40 lines of
C# just expanded into the full CloudFormation you would have hand-written. That expansion is the
value of CDK.

```bash
# diff: what would change vs. what is currently deployed?
cdk diff
```

On a first run `cdk diff` shows everything as "new" (`[+]`). Later, after the stack exists, it shows
*only* what your latest edit changes — like `git diff` for infrastructure. **Always read `cdk diff`
before `cdk deploy`.** It is how you avoid "I didn't know it would replace the database."

### A.7 — Build and push the image first (the service needs something to run)

The stack references an image in ECR. If ECR is empty, the tasks have nothing to pull and will
crash-loop. So build and push before you deploy the stack. From your *application* folder (where the
Dockerfile is):

```bash
# create the repo if you haven't (name must match A.5 step 4: "myapi")
aws ecr create-repository --repository-name myapi

# authenticate, build, tag, push
aws ecr get-login-password --region us-east-1 \
  | docker login --username AWS --password-stdin <acct>.dkr.ecr.us-east-1.amazonaws.com

docker build -t myapi .
docker tag myapi:latest <acct>.dkr.ecr.us-east-1.amazonaws.com/myapi:latest
docker push <acct>.dkr.ecr.us-east-1.amazonaws.com/myapi:latest
```

> **On an Apple Silicon / ARM laptop:** add `--platform linux/amd64` to `docker build`, or set
> `RuntimePlatform` to ARM in the task. A common first failure is "exec format error" in the ECS
> logs — that is an ARM image on an x86 task, or vice-versa. Match them.

### A.8 — Deploy

```bash
cd myapi-infra
cdk deploy
```

CDK shows you the security-group and IAM changes it will make and asks you to confirm (type `y`).
Then it creates everything — this takes **5–10 minutes**, mostly the load balancer and NAT gateway.
You will see a live list of resources going `CREATE_IN_PROGRESS → CREATE_COMPLETE`.

When it finishes, it prints your output:

```
Outputs:
MyapiInfraStack.ApiUrl = http://MyapiInfra-Api-XXXX.us-east-1.elb.amazonaws.com
```

### A.9 — Verify it works

```bash
# use the URL from the deploy output
curl http://MyapiInfra-Api-XXXX.us-east-1.elb.amazonaws.com/health
# expect: 200 and your health JSON
```

If it hangs or 503s, the tasks are not healthy yet. Give it a minute (the ALB waits for two
consecutive healthy checks). Then debug in this order:
1. **ECS console → your cluster → the service → Tasks tab.** Are tasks `RUNNING` or crash-looping?
2. **A stopped task → "Logs" tab** (or CloudWatch Logs). This shows *why* the container exited —
   usually a bad connection string, wrong port, or the ARM/x86 mismatch from A.7.
3. **Target group → Targets tab.** Are the tasks "healthy"? If "unhealthy," the health-check path or
   port is wrong — confirm it matches A.5 step 6 and your container's `EXPOSE`.

This debug loop — task status, then task logs, then target health — is the one you will use for
every future deploy. Learn it here.

### A.10 — Change something and redeploy (feel the loop)

Edit the stack — say, bump `DesiredCount` from 2 to 3 — then:

```bash
cdk diff        # shows ONLY: DesiredCount 2 -> 3
cdk deploy      # applies just that change, no downtime
```

That is the whole IaC workflow: **edit C# → `cdk diff` → `cdk deploy`**. To ship a new *app*
version, you push a new image tag and point the stack at it (in CI, the pipeline does this for you —
Rung 5).

### A.11 — Tear it all down (do this when you stop)

```bash
cdk destroy
```

Confirm with `y`. CDK deletes the service, the ALB, the NAT gateway, the VPC — everything the stack
created — in a couple of minutes. Your bill goes back to near-zero. Tomorrow, `cdk deploy` rebuilds
it identically. **That disposability is the entire reason IaC matters** — you just proved it.

(`cdk destroy` leaves the `CDKToolkit` bootstrap stack and your ECR images alone; those are cheap
and you want to keep them.)

### A.12 — What you just learned (and where it scales)

You now have the core CDK loop and the four constructs that carry most .NET web apps: **VPC,
Cluster, ApplicationLoadBalancedFargateService, and an ECR image**. From here, growth is additive —
you add more constructs to the same stack the same way:
- **Add a database:** a `DatabaseInstance` (Rung 4) in the VPC's private subnets, and
  `db.Secret.GrantRead(service.TaskDefinition.TaskRole)` to hand the app its credentials.
- **Add secrets/config:** `Secret.FromSecretNameV2(...)` and pass it via `TaskImageOptions.Secrets`.
- **Split environments:** instantiate the stack twice in `Program.cs` — `MyApiStack(app, "Dev", …)`
  and `MyApiStack(app, "Prod", …)` — with different `Env`, sizes, and counts. Same code, two
  environments. This is where IaC pays off hardest.
- **A custom domain + real TLS:** add an ACM certificate and a Route 53 record; set
  `Protocol = ApplicationProtocol.HTTPS` on the service so the ALB terminates TLS for you.

Every one of those is a few more lines in the same file, a `cdk diff`, and a `cdk deploy`. You have
the pattern now.

### A.13 — First-time errors, decoded

| Symptom | Cause | Fix |
|---|---|---|
| `Unable to resolve AWS account to use` | No credentials | `aws sts get-caller-identity` must work first (A.0) |
| `This stack uses assets, so the toolkit stack must be deployed` | Not bootstrapped | `cdk bootstrap` (A.1) |
| Tasks crash-loop, logs say `exec format error` | ARM image on x86 task (or reverse) | rebuild with `--platform linux/amd64` (A.7) |
| Targets stay "unhealthy", 503 from the URL | Health-check path/port wrong | match `Path`/`ContainerPort` to your app (A.5 steps 5–6) |
| Tasks can't pull the image | Empty ECR repo, or wrong repo name | push the image first; repo name in A.5 must match A.7 |
| `cdk deploy` hangs on rollback | New version never went healthy; circuit breaker rolled back | read the task logs — the app failed to start (usually config/DB) |
| Deploy is slow (~8 min) | Normal — ALB + NAT take time | wait; it is not stuck |
