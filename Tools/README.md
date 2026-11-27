# Extraction scripts (Python 3, no dependencies)

Used to reverse-engineer the HANDS OVER install while building this project. Re-run them after a game update.

- `dump_meta.py <global-metadata.dat> out.json`: types, fields, methods, string literals and constant values from IL2CPP metadata v39 (Unity 6000.3).
- `il2types.py <typeDefIndex>...`: resolves field and method signatures using `GameAssembly.dll`. Edit the paths at the top; the `Il2CppMetadataRegistration` offset is hard-coded for the current build.
- `sfile.py`: minimal Unity SerializedFile (v22) reader for `resources.assets` and `globalgamemanagers.assets`.
- `unityfs.py <bundle> <outdir>`: UnityFS/LZ4 extractor (used for the Addressables localization bundles).
- `gen_sdk_data.py`: regenerates the SDK's proxy shaders and Data/*.json from the extracted data.

The plugin's `Developer.DumpGameData` option is the easier way to refresh rigs and shaders.
