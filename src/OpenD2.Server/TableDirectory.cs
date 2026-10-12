using OpenD2.Assets;

namespace OpenD2.Server;

// Reads the owner's character tables (charstats.txt, experience.txt, skills.txt) from a directory the operator names with --tables.
// The files are plain TXT the owner extracted from their own install: the server has no MPQ or native dependency, never writes
// to the directory and never sends the files anywhere (clients only see the resulting skill and monster lists and a hash label).
public static class TableDirectory
{
    // Either the files directly, or the install layout data/global/excel. File names are matched without regard to case.
    private static readonly string[][] Layouts = [[], ["data", "global", "excel"]];

    public static CharacterTables Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string root = Path.GetFullPath(directory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Table directory not found: " + root);
        byte[] Read(string path)
        {
            string name = Path.GetFileName(path.Replace('\\', '/'));
            foreach (var layout in Layouts)
            {
                string folder = layout.Aggregate(root, Path.Combine);
                if (!Directory.Exists(folder)) continue;
                string? found = Directory.EnumerateFiles(folder).FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
                if (found is null) continue;
                if (new FileInfo(found).Length > ExcelTextTable.MaxBytes) throw new InvalidDataException($"{found} exceeds the {ExcelTextTable.MaxBytes} byte table budget.");
                return File.ReadAllBytes(found);
            }
            throw new FileNotFoundException($"'{name}' was not found in {root} (or in its data/global/excel folder).", name);
        }
        return CharacterTables.Load(Read);
    }
}
