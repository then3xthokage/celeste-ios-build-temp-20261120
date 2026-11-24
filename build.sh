#!/bin/bash
# colors
RESET="\033[0m"
RED="\033[0;31m"
BOLD_RED="\033[1;31m"
PINK="\033[0;95m"
BOLD_PINK="\033[1;95m"
CYAN="\033[0;96m"
BOLD_CYAN="\033[1;96m"
LIME="\033[0;92m"
BOLD_LIME="\033[1;92m"


# logging functions
error() {
	if [ "$2" == "1" ]; then
		echo -e "        ${RED}$1${RESET}"
	else
		echo -e "${BOLD_RED}(error)${RED} $1${RESET}"
	fi
}

unhandled_error() {
	error "unhandled error at line $1!"
	error "THIS IS MOST LIKELY A BUG"
	exit 1
}

warn() {
	echo -e "${BOLD_YELLOW}(warn)${YELLOW} $1${RESET}"
}

info() {
	echo -e "${BOLD_CYAN}(info)${CYAN} $1${RESET}"
}

info2() {
	echo -e "(info) $1${RESET}"
}

cd_fail() {
	error "failed to change directory - line $(caller)"
	exit 1
}

success() {
	if [ "$2" == "1" ]; then
		echo -e "          ${LIME}$1${RESET}"
	else
		echo -e "${BOLD_LIME}(success)${LIME} $1${RESET}"
	fi
}

# misc functions
update_submodules() {
	if [ "$1" == "1" ]; then
		if ! git submodule update --init --recursive; then error "failed to recursively update submodules"; exit 1; fi
	else
		if ! git submodule update --init; then error "failed to update submodules"; exit 1; fi
	fi
}

clone_repo() {
	local repo=$1
	local dir=$2

	if [ -d "${dir}/.git" ]; then
		info2 "$repo already cloned in $dir"
	else
		if ! git clone "$repo" "${@:2}"; then error "failed to clone git repo $repo with ${*:2}"; exit 1; fi
	fi
}

# trap all other errors
trap 'unhandled_error $LINENO' ERR

script_real_path=$(realpath -- "${BASH_SOURCE[0]}")
script_dir=$(dirname -- "$script_real_path")
cd "$script_dir" || cd_fail # make sure we're running in the script directory in case we get invoked from another directory
mv "$script_dir/.git-tmp" "$script_dir/.git" 2>/dev/null || echo a >/dev/null # in case the script got killed while applying patches

echo -e " ~ ${BOLD_PINK}Celeste ${RESET}${BOLD_CYAN}iOS${RESET} build script ~"
echo -e "  written by ${BOLD_PINK}rooot${RESET}"
echo -e "  https://github.com/RoootTheFox/celeste-ios"
echo -e "\n"

echo -e "This project has been tested on the ${BOLD_CYAN}Linux Steam build of Celeste 1.4.0.0${RESET} but SHOULD work on any other FNA-based Celeste build"
echo -e "Enter the full path to an ${BOLD_RED}UNMODIFIED${RESET}, ${BOLD_RED}FNA-based (!)${RESET} Celeste install: \c"
read -r celeste_path

# for some reason bash fails to check for relative paths, that's why we need to do this
#if ! [ -d "$celeste_path" ]; then
#	celeste_path="$(realpath $celeste_path)" # nvm this doesn't work either wtf (just gonna fail at relative paths then ig)
#fi

check_celeste_install() {
	local celeste_path="$1"

	# check if the things we need exist
	if ! [ -f "$celeste_path/Celeste.exe" ];	then echo "no_celeste_exe";	return; fi
	if ! [ -f "$celeste_path/FNA.dll" ];		then echo "no_fna";		return; fi
	if ! [ -f "$celeste_path/System.Xml.dll" ];	then echo "no_system_xml_dll";	return; fi
	if ! [ -f "$celeste_path/System.Core.dll" ];	then echo "no_system_core_dll";	return; fi
	if ! [ -d "$celeste_path/Content" ];		then echo "no_content";		return; fi

	# check if everest is installed (we do NOT want everest installed!)
	if [ -f "$celeste_path/everest-launch.txt" ];		then echo "modded"; return; fi
	if [ -f "$celeste_path/MonoMod.RuntimeDetour.dll" ];	then echo "modded"; return; fi
	if [ -f "$celeste_path/MonoMod.exe" ];			then echo "modded"; return; fi
	if [ -f "$celeste_path/MonoMod.Utils.dll" ];		then echo "modded"; return; fi
	if [ -f "$celeste_path/MMHOOK_Celeste.dll" ];		then echo "modded"; return; fi
	if [ -f "$celeste_path/MiniInstaller.exe" ];		then echo "modded"; return; fi
	if [ -f "$celeste_path/NLua.dll" ];			then echo "modded"; return; fi
	if [ -f "$celeste_path/KeraLua.dll" ];			then echo "modded"; return; fi
	if [ -f "$celeste_path/lua53.dll" ];			then echo "modded"; return; fi
	if [ -f "$celeste_path/Celeste.Mod.mm.dll" ];		then echo "modded"; return; fi
	if [ -f "$celeste_path/discord-rpc.dll" ];		then echo "modded"; return; fi

	# check if it's a Steam install of Celeste or not
	if [ -f "$celeste_path/Steamworks.NET.dll" ]; then
		echo "ok_steam"
	else
		echo "ok"
	fi
}

if ! [ -d "$celeste_path" ]; then
	error "the path you specified doesn't exist"
	exit 1
fi

info "verifying celeste install"
celeste_install_status=$(check_celeste_install "$celeste_path")

celeste_is_steam=0

case $celeste_install_status in
	"no_celeste_exe")
		error "your game install seems to be invalid (no Celeste.exe was found)"
		error "please make sure you pointed to the correct directory." 1
		exit 1
		;;
	"no_fna")
		error "your celeste install seems to not be FNA-based, which is needed for ios compatibility."
		error "please download a version of the game that uses FNA." 1
		error "on steam:" 1
		error " - the linux build uses FNA by default" 1
		error " - on windows, you can get the FNA version by selecting the 'opengl' beta for celeste on steam" 1
		error "on itch.io:" 1
		error " - download either the celeste linux build or the windows FNA build" 1
		error "on epic games:" 1
		error " - you should already be on the FNA version by default" 1
		exit 1
		;;
	"no_system_xml_dll")
		error "your celeste install seems to be missing System.Xml.dll"
		exit 1
		;;
	"no_system_core_dll")
		error "your celeste install seems to be missing System.Core.dll"
		exit 1
		;;
	"no_content")
		error "your celeste install seems to be missing the Content directory"
		error "please try redownloading the game." 1
		exit 1
		;;
	"modded")
		error "your celeste install appears to be modded!"
		error "please try redownloading the game or specifying the path to an unmodified install." 1
		exit 1
		;;
	"ok_steam")
		celeste_is_steam=1
		info "found steam build of celeste"
		;;
	"ok")
		celeste_is_steam=0
		info "found non-steam build of celeste"
		;;

	*)
		error "congratulations, you got an error that isn't possible!"
		error "'$celeste_install_status'" 1
		error "this might be a bug in this script" 1
		exit 1
esac

function get_fmod() {
	# ask to download FMOD
	info "please download the iOS version \"FMOD Engine\" version 1.10.09 (click 'Older') from https://www.fmod.com/download?version=1.10.09#fmodengine and open the .dmg"
	info "before proceeding! this SPECIFIC version of FMOD is required to build the game! please note that you while you need an account, the download is not paid!"
	read -r

	_fmod_path=$(mount |grep "FMOD Programmers API iOS" |sed 's/ (.*//g')
	fmod_path="${_fmod_path#*on }"

	if ! [ -d "$fmod_path" ]; then
		error "FMOD not found, please download and mount the .dmg and try again."
		get_fmod
	fi

	if ! head -n 10 "$fmod_path/FMOD Programmers API/doc/revision.txt"|grep "1.10.09"; then
		error "the FMOD image you downloaded appears to be a different version than 1.10.09, please unmount the image and download the correct version."
		get_fmod
	fi

	info "copying FMOD libraries from $fmod_path"
	if ! cp "$fmod_path/FMOD Programmers API/api/lowlevel/lib/libfmod_iphoneos.a" "$script_dir/celestemeow/libfmod.a"; then
		error "failed to copy libfmod_iphoneos.a"; exit 1
	fi
	if ! cp "$fmod_path/FMOD Programmers API/api/studio/lib/libfmodstudio_iphoneos.a" "$script_dir/celestemeow/libfmodstudio.a"; then
		error "failed to copy libfmodstudio_iphoneos.a"; exit 1
	fi
}

if ! [ -f "$script_dir/celestemeow/libfmod.a" ] || ! [ -f "$script_dir/celestemeow/libfmodstudio.a" ]; then
	info "fmod not found, prompting to download it"
	get_fmod
else
	info "fmod found"
fi

info "checking for required tools..."

if ! command -v git &>/dev/null; then missing_tool git "please install Xcode."; fi
if ! command -v dotnet &>/dev/null; then missing_tool dotnet "install Visual Studio for Mac and make sure the Xamarin.iOS and .NET workloads are selected"; fi
export PATH="$PATH:$HOME/.dotnet/tools"
if [ -n "${ILSPYCMD_DLL:-}" ]; then
	if ! [ -s "$ILSPYCMD_DLL" ]; then
		error "the preloaded ilspycmd assembly was not found"
		exit 1
	fi
	ilspycmd() { DOTNET_gcServer=0 DOTNET_GCConserveMemory=6 dotnet "$ILSPYCMD_DLL" "$@"; }
fi
if ! command -v ilspycmd &>/dev/null; then
	info "ilspycmd is not installed, attempting to install it using dotnet"
	if ! dotnet tool install --global ilspycmd --version 8.0.0.7246-preview3; then error "failed to install ilspycmd, please try installing it manually"; exit 1; fi

	if ! command -v ilspycmd &>/dev/null; then error "failed to invoke ilspycmd after installing, make sure it installed correctly"; exit 1; fi
fi

info "cloning submodules..."
update_submodules 0 # not doing --recursive because FNA's submodules are broken and will fail to clone
if ! git -C "$script_dir/Everest" submodule update --init --recursive; then error "failed to initialize Everest build dependencies"; exit 1; fi

cd "$script_dir/FNA" || cd_fail
clone_repo https://github.com/FNA-XNA/FAudio lib/FAudio --branch 21.03.05 --single-branch --recursive # --single-branch makes cloning faster
clone_repo https://github.com/FNA-XNA/FNA3D lib/FNA3D --branch 21.03 --single-branch # this one fails to clone submodules, don't use --recursive
clone_repo https://github.com/flibitijibibo/SDL2-CS lib/SDL2-CS # we need to checkout a specific commit here so we can't use --branch
clone_repo https://github.com/FNA-XNA/Theorafile lib/Theorafile # ^ same thing here

cd "$script_dir/FNA/lib/SDL2-CS" || cd_fail
if ! git checkout 2b8d237fd4585d14ea837764ac247d4cd200158f; then error "failed to checkout SDL2-CS"; exit 1; fi
cd "$script_dir/FNA/lib/Theorafile" || cd_fail
if ! git checkout 48d74afcbf838fe95ca56cec142efae07bb56f65; then error "failed to checkout Theorafile"; exit 1; fi

fna3d_path="$script_dir/FNA/lib/FNA3D"

cd "$fna3d_path" || cd_fail
clone_repo https://github.com/FNA-XNA/MojoShader MojoShader
clone_repo https://github.com/KhronosGroup/Vulkan-Headers Vulkan-Headers

cd "$fna3d_path/MojoShader" || cd_fail
git checkout c9037d90fa2f59b6be65d1391ca11d345356bad1
git submodule update --init --recursive

cd "$fna3d_path/Vulkan-Headers" || cd_fail
git checkout 85470b32ad5d0d7d67fdf411b6e7b502c09c9c52
git submodule update --init --recursive

cd "$fna3d_path" || cd_fail
git submodule update --init --recursive

cd "$script_dir/FNA" || cd_fail
# update submodules recursively to make sure we have everything we need
git submodule update --init --recursive

# apply required patches

apply_patch() {
	if [ "$2" == "1" ]; then mv "$script_dir/.git" "$script_dir/.git-tmp"; fi
	if git apply "$1" --reverse --check 2>/dev/null && ! git apply "$1" --check 2>/dev/null; then
		if [ "$2" == "1" ]; then mv "$script_dir/.git-tmp" "$script_dir/.git"; fi
		info2 "patch already applied"
	else
		if ! git apply "$1"; then
			if [ "$2" == "1" ]; then mv "$script_dir/.git-tmp" "$script_dir/.git"; fi
			error "failed to apply patch"
			exit 1
		fi
	fi
	if [ "$2" == "1" ]; then mv "$script_dir/.git-tmp" "$script_dir/.git"; fi
}

cd "$script_dir/FNA/lib/Theorafile" || cd_fail
apply_patch "$script_dir/patches/Theorafile-VidPlayer.patch"
cd "$script_dir/FNA" || cd_fail

info "applying the iOS Everest integration patch"
cd "$script_dir/Everest" || cd_fail
apply_patch "$script_dir/patches/Everest-ios.patch"
cd "$script_dir" || cd_fail

info "applying patches to FNA"
cd "$script_dir/FNA" || cd_fail
apply_patch "$script_dir/patches/FNA.patch"
cd "$script_dir/FNA/lib/FNA3D" || cd_fail
apply_patch "$script_dir/patches/FNA-FNA3D-renderer.patch"

info "pointing statically linked FNA libraries at the iOS executable"
python3 - "$script_dir/FNA" <<'PY'
import pathlib
import re
import sys

root = pathlib.Path(sys.argv[1])
libraries = ("SDL2", "FNA3D", "FAudio", "libtheorafile")
counts = {library: 0 for library in libraries}
ios_dynamic_imports = {
    "SDL2": {
        "SDL_AndroidBackButton",
        "SDL_AndroidGetActivity",
        "SDL_AndroidGetExternalStoragePath",
        "SDL_AndroidGetExternalStorageState",
        "SDL_AndroidGetInternalStoragePath",
        "SDL_AndroidGetJNIEnv",
        "SDL_AndroidRequestPermission",
        "SDL_GetAndroidSDKVersion",
        "SDL_IsAndroidTV",
        "SDL_IsChromebook",
        "SDL_IsDeXMode",
        "SDL_SetWindowsMessageHook",
        "SDL_WinRTGetDeviceFamily",
        "SDL_WinRTRunApp",
    }
}
dynamic_counts = {name: 0 for names in ios_dynamic_imports.values() for name in names}

def keep_unavailable_imports_dynamic(source, fallback_libraries):
    pattern = re.compile(
        r'(?P<prefix>\[\s*DllImport\s*\(\s*)'
        r'(?P<library>"__Internal"|nativeLibName)'
        r'(?P<arguments>[^\]]*\])'
        r'(?P<declaration>[^;{}]*?\b(?P<method>[A-Za-z_]\w*)\s*\()',
        re.S,
    )
    matches = list(pattern.finditer(source))
    for match in reversed(matches):
        method = match.group("method")
        entrypoint = re.search(r'\bEntryPoint\s*=\s*"([^"]+)"', match.group("arguments"))
        native_symbol = entrypoint.group(1) if entrypoint else method
        fallback = fallback_libraries.get(native_symbol) or fallback_libraries.get(method)
        if fallback is not None:
            start, end = match.span("library")
            source = source[:start] + f'"{fallback}"' + source[end:]
            dynamic_counts[native_symbol] += 1
    return source

for path in root.rglob("*.cs"):
    source = path.read_text()
    updated = source
    for library in libraries:
        literal = re.escape(library)
        const_pattern = re.compile(
            r'(\bconst\s+string\s+\w+\s*=\s*)"' + literal + r'"'
        )
        import_pattern = re.compile(
            r'(\[\s*(?:DllImport|LibraryImport)\s*\(\s*)"' + literal + r'"'
        )
        updated, const_count = const_pattern.subn(r'\1"__Internal"', updated)
        updated, import_count = import_pattern.subn(r'\1"__Internal"', updated)
        counts[library] += const_count + import_count
    updated = keep_unavailable_imports_dynamic(
        updated,
        {
            name: library
            for library, names in ios_dynamic_imports.items()
            for name in names
        },
    )
    if updated != source:
        path.write_text(updated)

missing = [library for library, count in counts.items() if count == 0]
if missing:
    raise SystemExit("Could not patch FNA native imports: " + ", ".join(missing))
print("Patched FNA native imports: " + ", ".join(f"{name}={count}" for name, count in counts.items()))
missing_dynamic = [name for name, count in dynamic_counts.items() if count == 0]
if missing_dynamic:
    raise SystemExit("Could not preserve unavailable SDL imports dynamically: " + ", ".join(missing_dynamic))
print("Kept platform-specific SDL imports dynamic: " + ", ".join(f"{name}={count}" for name, count in dynamic_counts.items()))
PY

cd "$script_dir" || cd_fail
info "recursively copying celeste game directory"
if [ "${CELESTE_GAME_INPUT_IN_PLACE:-0}" = "1" ]; then
	ln -s "$celeste_path" "$script_dir/_celeste_game"
else
	cp -a "$celeste_path/." "$script_dir/_celeste_game/"
fi

info "decompiling the game"
rm -rf _celeste_decomp 2>/dev/null
mkdir _celeste_decomp 2>/dev/null || echo a >/dev/null
if ! ilspycmd -o "_celeste_decomp" -p -d -r "_celeste_game" -lv CSharp7_3 "$script_dir/_celeste_game/Celeste.exe"; then
	error "failed to decompile the game!"
	exit 1
fi

# Disambiguate the FMOD type from System.Diagnostics.EventInstance in this game build.
python3 - "$script_dir/_celeste_decomp/Celeste/BadelineBoost.cs" <<'PY'
import pathlib
import re
import sys

path = pathlib.Path(sys.argv[1])
source = path.read_text()
alias = "using EventInstance = FMOD.Studio.EventInstance;\n"
if "using System.Diagnostics;" in source and "using FMOD.Studio;" in source and "EventInstance" in source and alias not in source:

    namespace = re.search(r"(?m)^namespace\s", source)
    if namespace is None:
        raise SystemExit("Could not locate namespace in BadelineBoost.cs")
    source = source[:namespace.start()] + alias + source[namespace.start():]
    path.write_text(source)
PY

info "patching the game"

cd "$script_dir/_celeste_decomp" || cd_fail
if [ "$celeste_is_steam" == "1" ]; then
	info2 "applying steam-specific patches"
	apply_patch "$script_dir/patches/remove-steam.patch" 1 # the 1 at the end will make the function temporarily rename the .git folder so git doesn't stop us from applying the patch
	apply_patch "$script_dir/patches/remove-steam2.patch" 1 # (fuck you git)
fi
python3 - "$script_dir/_celeste_decomp" <<'PY'
import pathlib
import re
import sys

root = pathlib.Path(sys.argv[1])
fmod_libraries = ("libfmodstudio", "libfmod", "fmodstudio", "fmod")
fmod_counts = {library: 0 for library in fmod_libraries}
fmod_dynamic_counts = {"FMOD_DSP_GetCPUUsage": 0}

def keep_unavailable_imports_dynamic(source, fallback_libraries):
    pattern = re.compile(
        r'(?P<prefix>\[\s*DllImport\s*\(\s*)'
        r'(?P<library>"__Internal"|nativeLibName)'
        r'(?P<arguments>[^\]]*\])'
        r'(?P<declaration>[^;{}]*?\b(?P<method>[A-Za-z_]\w*)\s*\()',
        re.S,
    )
    matches = list(pattern.finditer(source))
    for match in reversed(matches):
        method = match.group("method")
        entrypoint = re.search(r'\bEntryPoint\s*=\s*"([^"]+)"', match.group("arguments"))
        native_symbol = entrypoint.group(1) if entrypoint else method
        fallback = fallback_libraries.get(native_symbol) or fallback_libraries.get(method)
        if fallback is not None:
            start, end = match.span("library")
            source = source[:start] + f'"{fallback}"' + source[end:]
            fmod_dynamic_counts[native_symbol] += 1
    return source

for path in root.rglob("*.cs"):
    source = path.read_text()
    updated = source.replace("Assembly.GetEntryAssembly()", "Assembly.GetExecutingAssembly()")
    for library in fmod_libraries:
        pattern = re.compile(
            r'(\[\s*DllImport\s*\(\s*)"' + re.escape(library) + r'"'
        )
        updated, count = pattern.subn(r'\1"__Internal"', updated)
        fmod_counts[library] += count
    updated = keep_unavailable_imports_dynamic(updated, {"FMOD_DSP_GetCPUUsage": "fmod"})
    if updated != source:
        path.write_text(updated)

if sum(fmod_counts.values()) == 0:
    raise SystemExit("Could not find any FMOD DllImport declarations to link statically")
print("Patched FMOD native imports: " + ", ".join(f"{name}={count}" for name, count in fmod_counts.items()))
if fmod_dynamic_counts["FMOD_DSP_GetCPUUsage"] == 0:
    raise SystemExit("Could not preserve the unavailable FMOD_DSP_GetCPUUsage import dynamically")
print("Kept unavailable FMOD import dynamic: FMOD_DSP_GetCPUUsage")
settings = root / "Celeste" / "Settings.cs"
source = settings.read_text()
start = source.find('[XmlAnyElement("LaunchInDebugModeComment")]')
end = source.find("public int MaxScale", start)
if start >= 0 and end > start:
    settings.write_text(source[:start] + source[end:])
PY

info "enabled mod archives will be loaded through Everest"

info "building the patched game"
if ! dotnet publish --configuration Release -p:PlatformTarget=AnyCPU -p:Prefer32Bit=false; then error "failed to build the game!"; exit 1; fi

cd "$script_dir" || cd_fail

function copy_game_files() {
	if ! cp -r "$1" "$2"; then error "failed to copy '$1' to '$2'"; exit 1; fi
}

info "copying files"
mkdir "$script_dir/celeste" 2>/dev/null || echo a >/dev/null
copy_game_files "$script_dir/_celeste_decomp/bin/Release/net452/Celeste.exe" "$script_dir/celeste/Celeste.dll"
copy_game_files "$script_dir/_celeste_game/mscorlib.dll" "$script_dir/celeste"
copy_game_files "$script_dir/_celeste_game/Celeste.Content.dll" "$script_dir/celeste"
copy_game_files "$script_dir/_celeste_game/Content" "$script_dir/celestemeow/"
copy_game_files "$script_dir/misc/Celeste.dll.config" "$script_dir/celeste/"

info "building Everest against this Celeste and FNA version"
everest_runtime="$script_dir/everest-runtime"
rm -rf "$everest_runtime"
mkdir -p "$everest_runtime"
cd "$script_dir/Everest/external/MonoMod" || cd_fail
if ! dotnet publish "$script_dir/Everest/Celeste.Mod.mm/Celeste.Mod.mm.csproj" \
	--configuration Release \
	--framework net8.0 \
	--verbosity quiet \
	-p:EverestCelesteReference="$script_dir/celeste/Celeste.dll" \
	-p:EverestFNAReference="$celeste_path/FNA.dll" \
	-p:PublishDir="$everest_runtime/"; then
	error "failed to build Everest runtime"
	exit 1
fi
cd "$script_dir" || cd_fail

for required in Celeste.Mod.mm.dll MonoMod.Patcher.dll MonoMod.RuntimeDetour.HookGen.dll MonoMod.RuntimeDetour.dll MonoMod.Utils.dll Mono.Cecil.dll NLua.dll KeraLua.dll; do
	if ! [ -s "$everest_runtime/$required" ]; then
		error "Everest runtime is missing required assembly $required"
		exit 1
	fi
done

export MONOMOD_DEPDIRS="$everest_runtime:$script_dir/Everest/lib-ext:$celeste_path:$script_dir/celeste"
export MONOMOD_DEPENDENCY_MISSING_THROW=0

info "applying Everest patches and generating managed hook endpoints"
if ! dotnet "$everest_runtime/MonoMod.Patcher.dll" \
	"$script_dir/celeste/Celeste.dll" \
	"$script_dir/Everest/lib-ext" \
	"$everest_runtime/Celeste.Mod.mm.dll" \
	"$script_dir/celeste/Celeste.everest.dll"; then
	error "Everest failed to patch Celeste"
	exit 1
fi
if ! dotnet "$everest_runtime/MonoMod.Patcher.dll" \
	"$celeste_path/FNA.dll" \
	"$script_dir/Everest/lib-ext" \
	"$everest_runtime/Celeste.Mod.mm.dll" \
	"$script_dir/celeste/FNA.everest.dll"; then
	error "Everest failed to patch FNA"
	exit 1
fi
if ! dotnet "$everest_runtime/MonoMod.RuntimeDetour.HookGen.dll" --private \
	"$script_dir/celeste/Celeste.everest.dll" \
	"$script_dir/celeste/MMHOOK_Celeste.dll"; then
	error "Everest failed to generate Celeste hook endpoints"
	exit 1
fi
if ! dotnet "$everest_runtime/MonoMod.Patcher.dll" \
	"$script_dir/celeste/MMHOOK_Celeste.dll" \
	"$script_dir/Everest/lib-ext" \
	"$everest_runtime/Celeste.Mod.mm.dll" \
	"$script_dir/celeste/MMHOOK_Celeste.relinked.dll"; then
	error "Everest failed to route generated hooks through its compatibility layer"
	exit 1
fi
if ! mv "$script_dir/celeste/Celeste.everest.dll" "$script_dir/celeste/Celeste.dll" || \
	! mv "$script_dir/celeste/FNA.everest.dll" "$script_dir/celeste/FNA.everest.input.dll" || \
	! mv "$script_dir/celeste/MMHOOK_Celeste.relinked.dll" "$script_dir/celeste/MMHOOK_Celeste.dll"; then
	error "failed to install Everest patched assemblies"
	exit 1
fi

info "linking FNA native imports into the iOS application"
if ! dotnet run --project "$script_dir/tools/IOSNativeImportPatcher/IOSNativeImportPatcher.csproj" \
	--configuration Release -- "$script_dir/celeste/FNA.everest.input.dll" "$script_dir/celeste/FNA.dll"; then
	error "failed to rewrite FNA native imports for iOS"
	exit 1
fi
rm -f "$script_dir/celeste/FNA.everest.input.dll"

info "weaving Celeste, FNA, Everest, and hook endpoint assemblies for iOS"
for assembly in "$script_dir/celeste/Celeste.dll" "$script_dir/celeste/FNA.dll"; do
	if ! dotnet run --project "$script_dir/tools/IOSHookWeaver/IOSHookWeaver.csproj" \
		--configuration Release -- "$assembly" "$assembly.woven"; then
		error "failed to weave hook gates in $(basename "$assembly")"
		exit 1
	fi
	if ! mv "$assembly.woven" "$assembly"; then
		error "failed to install woven assembly $(basename "$assembly")"
		exit 1
	fi
done
cp "$script_dir/celeste/MMHOOK_Celeste.dll" "$everest_runtime/MMHOOK_Celeste.dll"
for assembly in "$everest_runtime"/*.dll; do
	runtime_assembly=$(basename "$assembly")
	if ! dotnet run --project "$script_dir/tools/IOSHookWeaver/IOSHookWeaver.csproj" \
		--configuration Release -- "$assembly" "$assembly.woven"; then
		error "failed to weave hook gates in runtime assembly $(basename "$assembly")"
		exit 1
	fi
	if ! mv "$assembly.woven" "$assembly"; then
		error "failed to install woven runtime assembly $(basename "$assembly")"
		exit 1
	fi
done

info "verifying the patched Celeste launch method for the iOS launcher"
if ! dotnet run --project "$script_dir/tools/IOSHookWeaver/IOSHookWeaver.csproj" \
	--configuration Release -- --verify-celeste-main "$script_dir/celeste/Celeste.dll"; then
	error "patched Celeste assembly does not contain the launch method used by the iOS app"
	exit 1
fi

info "checking that the patched Celeste assembly is portable to iOS arm64"
python3 - "$script_dir/celeste/Celeste.dll" <<'PY'
import pathlib
import struct
import sys

path = pathlib.Path(sys.argv[1])
data = path.read_bytes()
if len(data) < 64 or data[:2] != b"MZ":
    raise SystemExit(f"{path} is not a valid PE assembly")

pe_offset = struct.unpack_from("<I", data, 0x3C)[0]
if pe_offset + 24 > len(data) or data[pe_offset:pe_offset + 4] != b"PE\0\0":
    raise SystemExit(f"{path} has an invalid PE header")

machine, section_count = struct.unpack_from("<HH", data, pe_offset + 4)
optional_size = struct.unpack_from("<H", data, pe_offset + 20)[0]
optional = pe_offset + 24
magic = struct.unpack_from("<H", data, optional)[0]
if magic == 0x10B:
    directory_offset, directory_count_offset = optional + 96, optional + 92
elif magic == 0x20B:
    directory_offset, directory_count_offset = optional + 112, optional + 108
else:
    raise SystemExit(f"{path} has unsupported PE optional-header format {magic:#x}")

directory_count = struct.unpack_from("<I", data, directory_count_offset)[0]
if directory_count <= 14:
    raise SystemExit(f"{path} has no CLR data directory")
cli_rva, _ = struct.unpack_from("<II", data, directory_offset + 14 * 8)
section_table = optional + optional_size
sections = []
for index in range(section_count):
    section = section_table + index * 40
    virtual_size, virtual_address, raw_size, raw_offset = struct.unpack_from("<IIII", data, section + 8)
    sections.append((virtual_address, max(virtual_size, raw_size), raw_size, raw_offset))

cli_offset = None
for virtual_address, span, raw_size, raw_offset in sections:
    if virtual_address <= cli_rva < virtual_address + span:
        candidate = raw_offset + cli_rva - virtual_address
        if cli_rva - virtual_address >= raw_size:
            break
        cli_offset = candidate
        break
if cli_offset is None or cli_offset + 20 > len(data):
    raise SystemExit(f"{path} has an invalid CLR header")

flags = struct.unpack_from("<I", data, cli_offset + 16)[0]
if machine != 0x014C:
    raise SystemExit(f"{path} is not portable MSIL (PE machine is {machine:#06x})")
if not flags & 0x1:
    raise SystemExit(f"{path} contains mixed-mode code, which cannot run in the iOS managed runtime")
if flags & 0x2:
    raise SystemExit(f"{path} still requires a 32-bit process")
if flags & 0x20000:
    raise SystemExit(f"{path} still prefers a 32-bit process")

print(f"Verified portable MSIL Celeste assembly: PE machine {machine:#06x}, CorFlags {flags:#x}")
PY

mkdir -p "$script_dir/celestemeow/celeste"
copy_game_files "$script_dir/celeste/mscorlib.dll" "$script_dir/celestemeow/celeste/"
copy_game_files "$script_dir/celeste/Celeste.Content.dll" "$script_dir/celestemeow/celeste/"
copy_game_files "$script_dir/celeste/Celeste.dll.config" "$script_dir/celestemeow/celeste/"

# Bundle runtime assemblies from inside the iOS project tree. The iOS SDK rejects
# BundleResource items whose source paths are outside the app bundle directory.
runtime_resource_dir="$script_dir/celestemeow/RuntimeAssemblies"
rm -rf "$runtime_resource_dir"
mkdir -p "$runtime_resource_dir/Everest"
copy_game_files "$script_dir/celeste/Celeste.dll" "$runtime_resource_dir/Celeste.dll"
copy_game_files "$script_dir/celeste/FNA.dll" "$runtime_resource_dir/FNA.dll"
for runtime_assembly in "$everest_runtime"/*.dll; do
	copy_game_files "$runtime_assembly" "$runtime_resource_dir/Everest/"
done

info "building native libraries"
cd "$script_dir/fnalibs-ios-builder-celeste" || cd_fail

remove_duplicate_vorbis_objects() {
	local archive="$script_dir/celestemeow/libtheorafile.a"
	for object in smallft.o lpc.o; do
		if ar -t "$archive" | grep -Fxq "$object"; then
			if ! ar -d "$archive" "$object"; then
				error "failed to remove duplicate Vorbis object '$object' from libtheorafile.a"
				exit 1
			fi
		fi
	done
}

nativelib_build_err() {
	error "failed to build native libraries!"
	error "do you want to use the prebuilt libraries? [Y/n]\c" 1
	read -n 1 -r reply
	if [ "$reply" != "" ]; then echo; fi
	if [ "$reply" = "${reply#[Nn]}" ]; then
		if ! cp -r "$script_dir/fnalibs-ios-builder-celeste/prebuilt/." "$script_dir/celestemeow/"; then
			error "failed to copy prebuilt native libs!"
			exit 1
		fi
	else
		error "no native libraries, can't continue"
		exit 1
	fi
}

if [ "${USE_CACHED_NATIVE_LIBS:-0}" = "1" ]; then
	info "using cached iOS native libraries"
	for lib in libSDL2.a libFNA3D.a libFAudio.a libtheorafile.a libMoltenVK.a; do
		if ! [ -s "$script_dir/fnalibs-ios-builder-celeste/release/ios/device/$lib" ]; then
			error "cached native library '$lib' is missing"
			exit 1
		fi
	done
	if ! cp -r "$script_dir/fnalibs-ios-builder-celeste/release/ios/device/." "$script_dir/celestemeow/"; then
		error "failed to copy cached native libraries"
		exit 1
	fi
	remove_duplicate_vorbis_objects
else
	if ! ./updatelibs.sh; then nativelib_build_err; fi
	# The user's Celeste build ships FNA 24.1.0. Its native libraries must match
	# FNA 24.01 rather than the older revisions used by the upstream builder fork.
	pin_native_source() {
		local relative_path="$1"
		local revision="$2"
		local repo="$script_dir/fnalibs-ios-builder-celeste/$relative_path"
		if ! git -C "$repo" fetch --no-tags origin "$revision"; then
			error "failed to fetch compatible native source $relative_path@$revision"
			return 1
		fi
		if ! git -C "$repo" reset --hard; then
			error "failed to clear old native source patches in $relative_path"
			return 1
		fi
		if ! git -C "$repo" checkout --detach "$revision"; then
			error "failed to select compatible native source $relative_path@$revision"
			return 1
		fi
	}
	if ! pin_native_source SDL2 859844eae358447be8d66e6da59b6fb3df0ed778 || \
	   ! pin_native_source FNA3D 2a6f8586c8d032da18f707eb340bfbef26d1ff8b || \
	   ! pin_native_source FAudio b2bf5b385bb0719f6afc12fd3636be14280f4fbb || \
	   ! pin_native_source Theorafile 48d74afcbf838fe95ca56cec142efae07bb56f65; then
		error "failed to align native libraries with FNA 24.01"
		exit 1
	fi
	cd "$script_dir/fnalibs-ios-builder-celeste/FAudio" || cd_fail
	apply_patch "$script_dir/patches/FAudio-static-alignment.patch"
	cd "$script_dir/fnalibs-ios-builder-celeste" || cd_fail
	if ! git -C "$script_dir/fnalibs-ios-builder-celeste/FNA3D" submodule sync --recursive || \
	   ! git -C "$script_dir/fnalibs-ios-builder-celeste/FNA3D" submodule update --init --recursive; then
		error "failed to align FNA3D's pinned shader and Vulkan dependencies"
		exit 1
	fi
	cd "$script_dir/fnalibs-ios-builder-celeste/Theorafile" || cd_fail
	apply_patch "$script_dir/patches/Theorafile-VidPlayer.patch"
	cd "$script_dir/fnalibs-ios-builder-celeste" || cd_fail
	cd "$script_dir/fnalibs-ios-builder-celeste/FNA3D" || cd_fail
	apply_patch "$script_dir/patches/FNA-FNA3D-renderer.patch"
	cd "$script_dir/fnalibs-ios-builder-celeste" || cd_fail
	apply_patch "$script_dir/patches/fnalibs-ios-disable-opengl.patch"
	if [ "${ENABLE_VIRTUAL_CONTROLLER:-0}" = "1" ]; then
		info "enabling optional iOS virtual controller"
		cd "$script_dir/fnalibs-ios-builder-celeste/SDL2" || cd_fail
		apply_patch "$script_dir/patches/SDL-gamecontroller.patch"
		cd "$script_dir/fnalibs-ios-builder-celeste" || cd_fail
	else
		info "virtual on-screen controller disabled; SDL physical controller input remains enabled"
	fi
	if ! ./buildlibs.sh ios; then nativelib_build_err; fi
	if ! cp -r "$script_dir/fnalibs-ios-builder-celeste/release/ios/device/." "$script_dir/celestemeow/"; then nativelib_build_err; fi
	remove_duplicate_vorbis_objects
fi

cd "$script_dir" || cd_fail

theorafile_archive="$script_dir/celestemeow/libtheorafile.a"
for symbol in tf_audioinfo tf_close tf_eos tf_fopen tf_gettheorasize tf_granpos_dec \
	tf_hasaudio tf_hasvideo tf_open_callbacks tf_readaudio tf_readaudio2 \
	tf_readvideo tf_readvideo2 tf_reset tf_setaudiotrack tf_setvideotrack tf_videoinfo; do
	if ! nm -g "$theorafile_archive" | awk '{ symbol = $NF; sub(/^_/, "", symbol); print symbol }' | grep -Fxq "$symbol"; then
		error "the patched iOS Theorafile archive is missing required VidPlayer symbol '$symbol'"
		exit 1
	fi
done

info "collecting linked native imports from runtime-loaded managed assemblies"
native_import_manifest="$script_dir/ios-native-imports.txt"
managed_native_assemblies=( "$script_dir/celeste/"*.dll "$everest_runtime"/*.dll )
if ! dotnet run --project "$script_dir/tools/IOSNativeImportPatcher/IOSNativeImportPatcher.csproj" \
	--configuration Release -- --write-internal-imports "$native_import_manifest" "${managed_native_assemblies[@]}"; then
	error "failed to collect linked native imports from runtime assemblies"
	exit 1
fi

info "generating the native export list for runtime-loaded Celeste, FNA, and Everest code"
native_export_manifest="$script_dir/ios-native-exports.txt"
if ! cp "$native_import_manifest" "$native_export_manifest" || \
	! printf '%s\n' "_SDL_iPhoneSetEventPump" >> "$native_export_manifest"; then
	error "failed to initialize the native export list"
	exit 1
fi
native_archives=(
	"$script_dir/celestemeow/libSDL2.a"
	"$script_dir/celestemeow/libFNA3D.a"
	"$script_dir/celestemeow/libFAudio.a"
	"$script_dir/celestemeow/libtheorafile.a"
	"$script_dir/celestemeow/libMoltenVK.a"
	"$script_dir/celestemeow/libfmod.a"
	"$script_dir/celestemeow/libfmodstudio.a"
)
for archive in "${native_archives[@]}"; do
	if ! [ -s "$archive" ]; then
		error "native archive for export collection is missing: $archive"
		exit 1
	fi
	if ! (set -o pipefail; nm -gU "$archive" | awk 'NF >= 3 && $NF ~ /^_/ { print $NF }' >> "$native_export_manifest"); then
		error "failed to collect global native exports from $(basename "$archive")"
		exit 1
	fi
done
if ! LC_ALL=C sort -u "$native_export_manifest" -o "$native_export_manifest" || ! [ -s "$native_export_manifest" ]; then
	error "failed to finalize the native export list"
	exit 1
fi
info "collected $(wc -l < "$native_export_manifest" | tr -d ' ') native symbols for the iOS executable export list"

if ! dotnet publish "$script_dir/celestemeow/ci-ios.csproj" \
	--configuration Release \
	--runtime ios-arm64 \
	-p:EnableCodeSigning=false \
	-p:BuildIpa=true \
	-p:ValidateXcodeVersion=false \
	-p:IOSNativeExportsList="$native_export_manifest"; then
	error "failed to build ios project"
	exit 1
fi

info "moving built ipa to $script_dir"
ipa_path="$(find "$script_dir/celestemeow/bin" -type f -name '*.ipa' -print -quit)"
if [ -z "$ipa_path" ]; then
	error "the iOS app package was not generated"
	exit 1
fi
if ! python3 "$script_dir/tools/verify-ios-native-exports.py" "$ipa_path" --native-imports "$native_export_manifest"; then
	error "the packaged iOS app does not export every required runtime import and linked native library symbol"
	exit 1
fi
if ! mv "$ipa_path" "$script_dir/celestemeow.ipa"; then error "failed to move ipa"; exit 1; fi

success "done building celeste for ios!"
success "the ipa is located in '$script_dir' and can be installed using something like TrollStore or AltStore" 1
