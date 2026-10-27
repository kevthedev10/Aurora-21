#!/usr/bin/env bash
# Run inside the pinned, disposable Ubuntu image after copying source into /work/aurora21.
# Each invocation owns its temporary PostgreSQL cluster. No game or production listeners.
set -eu
report_root=${1:?report directory required}
revision=${2:?source revision required}
mode=${3:-stages}
case "$mode" in
    stages) profiles='80-40-true 80-10-false 80-10-true 40-20-true'; waits=false ;;
    waits) profiles='80-10-true 80-40-true'; waits=true ;;
    *) exit 2 ;;
esac
project=ultimateteam/server/tools/Aurora21.UltimateTeam.Performance
dotnet build ultimateteam/server/Aurora21.UltimateTeam.sln --configuration Release
dotnet test ultimateteam/server/Aurora21.UltimateTeam.sln --configuration Release --no-build --filter 'FullyQualifiedName~UnitOfWorkTests|FullyQualifiedName~PerformanceScenarioTests'
for profile in $profiles; do
    IFS=- read -r peers pool timings <<< "$profile"
    # Exit 1 is an expected latency failure, retained in run.json. Exit 2+ is a tooling failure.
    result=0
    dotnet run --project "$project" --configuration Release --no-build --no-restore -- \
        --seconds 60 --samples 50 --peers "$peers" --pool "$pool" --timings "$timings" --waits "$waits" \
        --revision "$revision" --output "$report_root/$profile" || result=$?
    if [ "$result" -gt 1 ] || [ ! -f "$report_root/$profile/run.json" ]; then
        exit 2
    fi
done
dotnet test ultimateteam/server/Aurora21.UltimateTeam.sln --configuration Release --no-build
dotnet format ultimateteam/server/Aurora21.UltimateTeam.sln --verify-no-changes --no-restore
