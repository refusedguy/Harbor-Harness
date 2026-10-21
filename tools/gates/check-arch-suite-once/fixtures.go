package archsuiteonce

// fixtureWorkflow is the python original's FIXTURE_WORKFLOW, byte for byte.
// The fixture is the same one, so a port that changed the parser's
// reading of it fails here.
const fixtureWorkflow = `name: ci

on:
  pull_request:

jobs:
  build:
    runs-on: ubuntu-24.04
    env:
      HarborArchGate: ${{ github.event_name == 'pull_request' && 'false' || 'true' }}
    steps:
      - uses: actions/checkout@v7
      - name: Build solution via NUKE
        run: bash build/build.sh Compile --configuration Release
  reference-trimmer:
    runs-on: ubuntu-24.04
    steps:
      - uses: actions/checkout@v7
      - name: Build the solution and collect the analyzer's findings
        run: |
          dotnet build Harbor.slnx -c Release --no-restore \
            -p:HarborReferenceTrimmer=true \
            -p:HarborArchGate=false \
            -p:MaxCpuCount=0
  test:
    runs-on: ubuntu-24.04
    strategy:
      matrix:
        shard: [core, platform, ui, e2e]
        include:
          - shard: core
            runner: ubuntu-24.04-arm
            guard: true
            projects: >-
              Harbor.Abstractions.Tests
              Harbor.Architecture.Tests
              Harbor.Core.Tests
          - shard: platform
            runner: ubuntu-24.04-arm
            guard: true
            projects: >-
              Harbor.Tools.Builtin.Tests
              Harbor.Storage.Tests
          - shard: ui
            runner: ubuntu-24.04-arm
            guard: true
            projects: >-
              Harbor.Tui.Tests
              Harbor.Ui.Framework.Tests
          - shard: e2e
            runner: ubuntu-24.04-arm
            guard: true
            projects: >-
              Harbor.E2E.Cli
              Harbor.App.Cli.Tests
    steps:
      - uses: actions/checkout@v7
      - name: Run shard ${{ matrix.shard }}
        run: |
          for p in ${{ matrix.projects }}; do
            dotnet exec "tests/$p/bin/Release/net10.0/$p.dll"
          done
`

// fixtureProps is the python original's FIXTURE_PROPS, byte for byte.
const fixtureProps = `<Project>
  <PropertyGroup>
    <HarborArchGate Condition="'$(HarborArchGate)' == ''">true</HarborArchGate>
  </PropertyGroup>
  <Target Name="HarborArchitectureGate"
          AfterTargets="Build"
          Condition="'$(Configuration)' == 'Release' AND '$(HarborArchGate)' == 'true' AND '$(MSBuildProjectName)' == 'Harbor.Architecture.Tests'">
  </Target>
</Project>
`
