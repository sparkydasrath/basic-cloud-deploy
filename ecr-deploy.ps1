$acct = ""
$region = "us-east-1"
$repo = "dotnet/basicdeploy"
$sha = git rev-parse --short HEAD

$target = "${acct}.dkr.ecr.${region}.amazonaws.com/${repo}:$sha"

docker tag basicapp:latest $target
docker image inspect $target
docker push $target