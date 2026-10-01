namespace PS5Craft.Services;

/// <summary>
/// sce_sys entries that fpkg-cli merges from the package CNT so the tree can be rebuilt as an FPKG.
/// Publishing Tools regenerates them when building a package; they are not part of the game file system
/// and must not end up in an image that is mounted and launched as a folder.
/// </summary>
public static class PackageOnlyFiles
{
    public static readonly IReadOnlyList<string> SceSysNames =
    [
        "license.dat",
        "license.info",
        "playgo-chunk.dat",
        "playgo-chunk.crc",
        "playgo-hash-table.dat",
        "playgo-ficm.dat",
        "playgo-scenario.json",
        "playgo-manifest.xml",
        "origin-param.json",
        "target-param.json",
        "origin-deltainfo.dat",
        "target-deltainfo.dat",
        "origin-relocinfo.dat",
        "target-relocinfo.dat",
        "pfs-region-hints.json",
        "imagedigs.dat",
        "pfsimage.xml",
        "param_cp_values.json"
    ];

    /// <summary>Deletes the package-only entries from <c>&lt;root&gt;/sce_sys</c>; returns the relative paths removed.</summary>
    public static IReadOnlyList<string> Remove(string gameRoot)
    {
        var sceSys = Path.Combine(gameRoot, "sce_sys");
        var removed = new List<string>();
        if (!Directory.Exists(sceSys))
        {
            return removed;
        }

        foreach (var name in SceSysNames)
        {
            var path = Path.Combine(sceSys, name);
            if (File.Exists(path))
            {
                File.Delete(path);
                removed.Add("sce_sys/" + name);
            }
        }

        return removed;
    }
}
