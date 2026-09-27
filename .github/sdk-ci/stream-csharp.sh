set -eu
test ! -e /work
mkdir /results/consumer
cp /suite/csharp/Consumer.csproj /suite/csharp/NuGet.Config /results/consumer/
cp /sdk/conformance/stream-csharp/Program.cs /results/consumer/
sed -i 's|/cache/recording-packages|/artifacts|' /results/consumer/NuGet.Config
export REACON_DOTNET_TFM=net10.0
dotnet restore /results/consumer/Consumer.csproj --configfile /results/consumer/NuGet.Config --force --no-cache --nologo
REACON_RETAINED_NUPKG="/artifacts/Reacon.Sdk.$REACON_SDK_PACKAGE_VERSION.nupkg" \
  dotnet run --project /results/consumer/Consumer.csproj -c Release --no-restore --nologo
