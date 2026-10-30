#!/usr/bin/env python3
"""Copies the license files of everything shipped in the Windows exe into one folder (embedded in the exe by
windows/PdfSvd.csproj): pdf-svd, PDFium, the NuGet packages of the last restore and the .NET runtime pack.
Usage: scripts/collect-licenses-windows.py <dest>   (after `dotnet restore -r win-x64`)"""
import glob, json, os, re, shutil, sys

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
dest = sys.argv[1]
shutil.rmtree(dest, ignore_errors=True)
os.makedirs(dest)

shutil.copy(os.path.join(root, "LICENSE"), os.path.join(dest, "pdf-svd.txt"))
shutil.copy(os.path.join(root, "THIRD-PARTY-NOTICES.md"), os.path.join(dest, "THIRD-PARTY-NOTICES.md"))
for path in glob.glob(os.path.join(root, "Vendor/pdfium-windows/licenses/*")):
    shutil.copy(path, dest)

# Build-time only packages: nothing of them ends up in the exe.
BUILD_ONLY = ("microsoft.net.illink.tasks", "avalonia.buildservices")
LICENSE_FILE = re.compile(r"^(license|third-party-notices)[^/]*$", re.IGNORECASE)

assets = json.load(open(os.path.join(root, "windows/obj/project.assets.json")))
package_dir = next(iter(assets["packageFolders"]))
for name, info in assets["libraries"].items():
    package_id = name.split("/")[0].lower()
    if info.get("type") != "package" or package_id in BUILD_ONLY:
        continue
    if ".nativeassets." in package_id and not package_id.endswith(".win32"):
        continue
    package = os.path.join(package_dir, info["path"])
    for file in info.get("files", []):
        if LICENSE_FILE.match(file):
            shutil.copy(os.path.join(package, file), os.path.join(dest, f"{name.split('/')[0]}-{file}"))

# .NET runtime pack (self-contained exe): not listed among the libraries.
framework = next(iter(assets["project"]["frameworks"].values()))
packs = [d for d in framework["downloadDependencies"] if d["name"].lower().startswith("microsoft.netcore.app.runtime.win-")]
for pack in packs[:1]:
    folder = os.path.join(package_dir, pack["name"].lower(), pack["version"].strip("[]").split(",")[0].strip())
    for file in ("LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT"):
        if os.path.exists(os.path.join(folder, file)):
            shutil.copy(os.path.join(folder, file), os.path.join(dest, f"dotnet-runtime-{file}"))

print(f"{len(os.listdir(dest))} fichiers de licence dans {dest}")
