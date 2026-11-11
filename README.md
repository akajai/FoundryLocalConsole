# FoundryLocalConsole — test Microsoft Foundry Local on the Mac GPU

Standalone console app that loads a model with Microsoft.AI.Foundry.Local from the
external drive, runs a warm-up and a BHRF-style voice-note → JSON extraction, and
prints load time, latency and tokens/s. Uses the same FoundryPhiChatClient (IChatClient)
that BHRF will use.

## Run
mkdir -p "/Volumes/Crucial X9/BHRFMoldels/foundry"
cd FoundryLocalConsole
dotnet run                       # tests
dotnet run -- --chat             # tests + interactive chat

First run downloads phi-3.5-mini into ModelCacheDir (several GB); later runs load from the drive.

## Switch model
- Another catalog model:  dotnet run -- --FoundryLocal:ModelAlias=phi-4-mini
- Your own MediPhi ONNX (built with onnxruntime_genai.models.builder -e webgpu):
  set "LocalModelPath" in appsettings.json to the output folder.

## Check the GPU
Activity Monitor > Window > GPU History while the extraction runs.

## Notes
- Do not add Microsoft.ML.OnnxRuntime* packages; Foundry Local brings its own
  (adding them causes the "Class ... is implemented in both" objc warnings).
- Floating versions (*) pull the latest Microsoft.Extensions packages; pin them if you prefer.

## Profiles (all Phi / MediPhi models)
appsettings.json has a "Profiles" list; pick one with "ActiveProfile" or on the command line:
  dotnet run -- --list
  dotnet run -- --FoundryLocal:ActiveProfile=mediphi-instruct
  dotnet run -- --FoundryLocal:ActiveProfile=phi-4-mini --chat

Catalog profiles (phi-3.5-mini, phi-4-mini, phi-4) download automatically.
MediPhi profiles need the ONNX folders first:
  bash build-mediphi-onnx.sh                    # all 7 (~2.5 GB each in BHRFMoldels/onnx)
  bash build-mediphi-onnx.sh MediPhi-Instruct   # just one
