# Third-party dependencies

Desktop: [Avalonia](https://github.com/AvaloniaUI/Avalonia), MIT, with its bundled native and graphics dependencies. Preserve the accompanying notices when redistributing published packages.

Server image thumbnails: [SkiaSharp](https://github.com/mono/SkiaSharp), MIT, with Skia and its bundled third-party codecs. Keep the SkiaSharp native library and the upstream license and notices when distributing the server package. Thumbnail caches are disposable and are regenerated after migration.

SMB gateway: [SMBLibrary and SMBLibrary.Adapters](https://github.com/TalAloni/SMBLibrary), LGPL-3.0-or-later; [DiskAccessLibrary](https://github.com/TalAloni/DiskAccessLibrary), LGPL-3.0-or-later. The gateway uses the separately distributed, replaceable managed assemblies from official NuGet packages. It does not statically incorporate them into the Native AOT main server. The upstream repositories provide source, license texts and build instructions. A commercial SMBLibrary license is also available from its author.

The .NET self-contained SMB gateway also includes Microsoft .NET runtime components with their shipped license and third-party notices. Retain those files from the publish directory. No SMB library source has been copied into application source; files under `.test-data/smb-reference` are development references only and are not shipped.
