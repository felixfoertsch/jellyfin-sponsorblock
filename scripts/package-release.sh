#!/usr/bin/env bash
set -euo pipefail

# Description: Build a Jellyfin SponsorBlock release zip.
# Usage: ./scripts/package-release.sh <version>

main() {
	if [[ $# -ne 1 ]]; then
		echo "Usage: ./scripts/package-release.sh <version>" >&2
		exit 2
	fi

	local version="$1"
	local script_dir
	script_dir="$(dirname "${BASH_SOURCE[0]}")"
	local root
	pushd "$script_dir/.." >/dev/null
	root="$(pwd -P)"
	popd >/dev/null

	cd "$root"

	dotnet build Jellyfin.Plugin.SponsorBlock -c Release

	local output_dir="$root/artifacts"
	mkdir -p "$output_dir"

	local zip_path="$output_dir/jellyfin-plugin-sponsorblock-$version.zip"
	rm -f "$zip_path"

	local dll_path="$root/Jellyfin.Plugin.SponsorBlock/bin/Release/net9.0/Jellyfin.Plugin.SponsorBlock.dll"
	test -f "$dll_path"

	zip -j "$zip_path" "$dll_path"

	local entry_count
	entry_count="$(zipinfo -1 "$zip_path" | wc -l | tr -d ' ')"
	if [[ "$entry_count" != "1" ]]; then
		echo "Unexpected zip entry count: $entry_count" >&2
		zipinfo -1 "$zip_path" >&2
		exit 1
	fi

	local entry_name
	entry_name="$(zipinfo -1 "$zip_path")"
	if [[ "$entry_name" != "Jellyfin.Plugin.SponsorBlock.dll" ]]; then
		echo "Unexpected zip entry: $entry_name" >&2
		exit 1
	fi

	md5 -q "$zip_path"
}

main "$@"
