using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using CsvHelper;

// Reads BenchmarkDotNet CSV format results from two directories and creates a rudimentary diff view.

DirectoryInfo oldDir, newDir;
string targetDir;

switch (args)
{
    case [var oldDirArg, var newDirArg]:
        oldDir = FindDirectory(oldDirArg);
        newDir = FindDirectory(newDirArg);
        targetDir = Directory.GetCurrentDirectory();
        break;
    case [var oldDirArg, var newDirArg, var targetDirArg]:
        oldDir = FindDirectory(oldDirArg);
        newDir = FindDirectory(newDirArg);
        targetDir = targetDirArg;
        break;
    default:
        Console.Error.WriteLine("syntax: <old result path> <new result path> [target dir to save files to]");
        return 1;
}

var columns = new List<string>
{
    "Type",
    "Method",
    "FileName",
    "N",
    "Mean",
    "Error",
    "Gen 0/1k Op",
    "Gen 1/1k Op",
    "Gen 2/1k Op",
    "Allocated Memory/Op",
    "Gen 0",
    "Gen 1",
    "Gen 2",
    "Allocated"
};

var oldDirName = oldDir.Name != newDir.Name ? oldDir.Name : oldDir.Parent.Name;
var newDirName = newDir.Name != oldDir.Name ? newDir.Name : newDir.Parent.Name;
var targetFile = Path.Combine(targetDir, oldDirName + "_vs_" + newDirName + "-github.md");
using var writer = new StreamWriter(targetFile);

foreach (var (oldFile, newFile) in CreateFilePairs(oldDir, newDir))
{
    writer.WriteLine("## " + oldFile.Name.Replace("-report.csv", "", StringComparison.Ordinal));
    writer.WriteLine();

    Console.WriteLine("Analyzing pair " + oldFile.Name);

    using var oldReader = new CsvReader(new StreamReader(oldFile.FullName), CultureInfo.InvariantCulture);
    using var newReader = new CsvReader(new StreamReader(newFile.FullName), CultureInfo.InvariantCulture);

    _ = oldReader.Read();
    _ = newReader.Read();

    _ = oldReader.ReadHeader();
    _ = newReader.ReadHeader();

    var effectiveHeaders = columns
        .Where(x => oldReader.TryGetField(x, out string _) || newReader.TryGetField(x, out string _))
        .ToList();

    writer.WriteLine("| **Diff**|" + string.Join("|", effectiveHeaders) + "|");

    writer.Write("|------- ");
    foreach (var effectiveHeader in effectiveHeaders)
    {
        writer.Write("|-------");
        if (effectiveHeader.IndexOf("Gen ", StringComparison.OrdinalIgnoreCase) > -1 || effectiveHeader is "Allocated" or "Mean")
        {
            writer.Write(":");
        }
    }

    writer.WriteLine("|");

    while (oldReader.Read() && newReader.Read())
    {
        var oldColumnValues = new Dictionary<string, string>();

        writer.Write("| Old |");
        foreach (var effectiveHeader in effectiveHeaders)
        {
            var value = "-";
            if (oldReader.TryGetField(effectiveHeader, out string temp))
            {
                value = temp;
            }

            oldColumnValues[effectiveHeader] = value;
            writer.Write(value + "|");
        }

        writer.WriteLine();

        writer.Write("| **New** |");
        foreach (var effectiveHeader in effectiveHeaders)
        {
            if (effectiveHeader is "Type" or "Method" or "N" or "FileName")
            {
                writer.Write("\t|");
            }
            else
            {
                var value = "-";
                if (newReader.TryGetField(effectiveHeader, out string temp))
                {
                    value = temp;
                }

                if (oldColumnValues.TryGetValue(effectiveHeader, out var oldString))
                {
#pragma warning disable IDE0042 // Deconstruct variable declaration
                    var oldResult = SplitResult(oldString);
                    var newResult = SplitResult(value);
#pragma warning restore IDE0042 // Deconstruct variable declaration

                    if (string.IsNullOrWhiteSpace(oldResult.Unit) == string.IsNullOrWhiteSpace(newResult.Unit))
                    {
                        var canCalculateDiff = effectiveHeader is not "Error"
                                               && oldResult.Value is not "-" and not "N/A" and not "NA"
                                               && newResult.Value is not "-" and not "N/A" and not "NA"
                                               && decimal.TryParse(oldResult.Value, out var tempOldResult) && tempOldResult != 0;

                        decimal newMultiplier = 1;
                        const decimal conversionFromBigger = 0.0009765625M;

                        if (canCalculateDiff && oldResult.Unit.Length > 0)
                        {
                            switch (oldResult.Unit, newResult.Unit)
                            {
                                case var (oldUnit, newUnit) when oldUnit == newUnit:
                                    // ok
                                    break;
                                case ("MB", "KB"):
                                case ("KB", "B"):
                                case ("GB", "MB"):
                                case ("s" , "ms"):
                                case ("ms", "us"):
                                case ("ms", "μs"):
                                case ("μs", "ns"):
                                    newMultiplier = conversionFromBigger;
                                    break;
                                case ("MB", "B"):
                                    newMultiplier = conversionFromBigger * conversionFromBigger;
                                    break;
                                case ("ms", "s"):
                                case ("μs", "ms"):
                                case ("KB", "MB"):
                                    newMultiplier = 1 / conversionFromBigger;
                                    break;
                                default:
                                    canCalculateDiff = false;
                                    break;
                            }
                        }

                        if (canCalculateDiff)
                        {
                            var old = decimal.Parse(oldResult.Value, CultureInfo.InvariantCulture);
                            var newValue = decimal.Parse(newResult.Value, CultureInfo.InvariantCulture);

                            var diff = (newValue * newMultiplier / old - 1) * 100;
                            value += $" ({diff:+#;-#;0}%)";
                        }
                        else if ((oldResult.Value, newResult.Value) is ("-", _) or (_, "-") or ("0.0000", "0.0000"))
                        {
                            // OK
                        }
                        else if (decimal.TryParse(oldResult.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out _)
                                 && newResult.Value is "-")
                        {
                            value += " (-100%)";
                        }
                        else
                        {
                            if (effectiveHeader is not "Error" && oldString != value)
                            {
                                Console.Error.WriteLine("Cannot calculate diff for " + oldString + " vs " + value);
                            }
                        }
                    }
                }

                writer.Write(" **" + value + "** |");
            }
        }

        writer.WriteLine();
    }

    writer.WriteLine();
    writer.WriteLine();
}

writer.Close();

Console.WriteLine("Wrote results to " + targetFile);

return 0;

static (string Value, string Unit) SplitResult(string result) =>
    result.LastIndexOf(' ') is var idx and >= 0
        ? (result[..idx], result[(idx + 1)..])
        : (result, "");

static IEnumerable<(FileInfo OldFile, FileInfo NewFile)> CreateFilePairs(DirectoryInfo oldDir, DirectoryInfo newDir)
{
    foreach (var oldReportFile in oldDir.GetFiles("*-report.csv"))
    {
        var fileName = oldReportFile.Name;
        var newReportFile = new FileInfo(Path.Combine(newDir.FullName, fileName));
        if (newReportFile.Exists)
        {
            yield return (oldReportFile, newReportFile);
        }
        else
        {
            // check if new file name format without namespace
            if (fileName.Split('.') is [.., var token1, var token2])
            {
                fileName = token1 + "." + token2;
                newReportFile = new FileInfo(Path.Combine(newDir.FullName, fileName));
                if (newReportFile.Exists)
                {
                    yield return (oldReportFile, newReportFile);
                }
            }
        }
    }
}

static DirectoryInfo FindDirectory(string path)
{
    var dir = new DirectoryInfo(path);
    if (!dir.Exists)
    {
        Console.Error.WriteLine("directory does not exist: " + path);
    }

    if (dir.GetFiles("*.csv") is [])
    {
        if (dir.GetDirectories().FirstOrDefault(x => x.Name is "results") is { } resultsDirectory)
        {
            dir = resultsDirectory;
        }
    }

    return dir;
}
