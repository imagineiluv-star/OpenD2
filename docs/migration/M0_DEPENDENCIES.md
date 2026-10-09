# M0 의존성과 소스 재빌드

M0 자체 DLL은 `src/`의 C# 소스로 빌드한다. C++에 포함된 기존 바이너리는 신규 C# 실행 경로에서 참조하지 않는다. 타사 엔진 전체를 포크에 복사한 상태는 아니며 아래 고정 버전 소스를 추적한다.

| 의존성 | 고정 버전 / 출처 | 소스 / 라이선스 | 재빌드 경로 |
|---|---|---|---|
| .NET SDK | 10.0.401, global.json | dotnet/sdk, MIT 및 THIRD-PARTY-NOTICES | 공식 SDK source-build 절차; M0는 공식 배포 SDK 사용 |
| .NET runtime | SDK에 포함된 10.0.12 | dotnet/runtime v10.0.12, MIT 및 THIRD-PARTY-NOTICES | runtime 저장소 빌드 문서; export가 OS별 런타임 포함 |
| Godot editor/templates | 4.7.2-stable .NET | godotengine/godot 4.7.2-stable, MIT 및 COPYRIGHT.txt | Godot 공식 .NET 소스 빌드 절차 |
| Godot.NET.Sdk / GodotSharp / GodotSharpEditor / Godot.SourceGenerators | 4.7.2 | 같은 Godot 소스의 modules/mono | 엔진 glue 생성 → GodotSharp assemblies 빌드 |
| 그 외 게임/MPQ 라이브러리 | 아직 채택 없음 | 후보 평가 M1-02 | 실제 채택 시 추가 |

- [Godot 소스](https://github.com/godotengine/godot/tree/4.7.2-stable)
- [Godot .NET 빌드 문서](https://docs.godotengine.org/en/stable/contributing/development/compiling/compiling_with_dotnet.html)
- [SDK 소스](https://github.com/dotnet/sdk), [runtime 소스](https://github.com/dotnet/runtime/tree/v10.0.12)
- [Godot 배포본과 공식 해시](https://github.com/godotengine/godot-builds/releases/tag/4.7.2-stable)

Godot 빌드는 SCons에서 `module_mono_enabled=yes`로 편집기를 빌드하고, 해당 편집기의 `--headless --generate-mono-glue modules/mono/glue`로 glue를 생성한 뒤 `modules/mono/build_scripts/build_assemblies.py`를 실행한다. 플랫폼별 도구와 인수는 위 버전의 공식 문서를 따른다. **M0에서 엔진/런타임 자체의 소스 재빌드는 수행하지 않았다.** 공식 배포본의 해시 확인과 자체 프로젝트 재빌드 범위를 구분한다.

`eng/toolchain.json`은 편집기 3종과 export template의 공식 SHA-512를 고정한다. NuGet 의존성은 각 `packages.lock.json`에 contentHash로 고정한다. export의 RID별 복원은 별도 런타임 패키지를 가져오며, RID별 lock 업데이트는 export 전용 obj 아래로 분리한다.

배포 전 Godot COPYRIGHT.txt와 .NET 및 전이 의존성의 고지 파일을 패키지에 동봉하고 확인해야 한다. 원본 게임 리소스는 이 저장소/CI에 올리지 않는다. 기존 C++ `Libraries/`의 출처·재빌드 공백은 해소되지 않았으며 M1 의존성 선정과 별도 이관 항목으로 추적한다.
