import os
import zipfile
import glob
from io import StringIO

def get_git_version_and_branch():
    try:
        version = os.popen("git rev-list --count HEAD").read().strip()
        branch = os.popen("git rev-parse --abbrev-ref HEAD").read().strip()
        int(version)  # Ensure version is an integer
        return version, branch
    except ValueError:
        print("not a git repository?")
        return "0", "unknown"

def format_version(version):
    minor = version[-1:]
    major = version[:-1]
    return f"{major}.{minor}"

def add_files_to_zip(zip_file, base_path, pattern, prefix='', recursive=True):
    for file in glob.glob(pattern, recursive=recursive):
        zipname = os.path.join(prefix, os.path.relpath(file, base_path))
        zip_file.write(file, zipname)

def main():
    version, branch = get_git_version_and_branch()
    formatted_version = format_version(version)

    print(f"version: {formatted_version}")

    # Prepare version file content
    version_f = StringIO()
    version_f.write(f"__version__ = '{formatted_version}'\n")
    version_f.write(f"branch = '{branch}'\n")
    
    # Ensure the release directory exists
    release_dir = 'release'
    os.makedirs(release_dir, exist_ok=True)

    # Clean up old publish files if they exist
    publish_path = "./BlenderUmap/bin/Publish/"
    for f in glob.glob(publish_path + "**", recursive=True):
        try:
            os.remove(f)
        except FileNotFoundError:
            pass

    targets = ["osx.12-x64", "win-x64", "linux-x64"]
    for target in targets:
        dotnet_command = (
            f"dotnet publish BlenderUmap -c Release -r {target} --no-self-contained "
            f"-o \"{publish_path}\" -p:PublishSingleFile=true -p:DebugType=None "
            f"-p:DebugSymbols=false -p:IncludeAllContentForSelfExtract=true "
            f"-p:AssemblyVersion={formatted_version} -p:FileVersion={formatted_version}"
        )
        
        print(f"Running: {dotnet_command}")
        code = os.system(dotnet_command)
        if code != 0:
            raise Exception(f"dotnet publish failed with code {code}")

        zip_filename = f'{release_dir}/BlenderUmap-{formatted_version}-{target}.zip'
        with zipfile.ZipFile(zip_filename, 'w', zipfile.ZIP_LZMA, allowZip64=True, compresslevel=9) as zipf:
            add_files_to_zip(zipf, publish_path, publish_path + "**", "BlenderUmap/", False)
            add_files_to_zip(zipf, "./Importers/Blender/", "./Importers/Blender/**/*.py", "BlenderUmap/", True)
            zipf.writestr("BlenderUmap/__version__.py", version_f.getvalue())

    version_f.close()

if __name__ == "__main__":
    main()
