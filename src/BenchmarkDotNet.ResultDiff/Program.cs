using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using Dsv;
using MoreLinq;

// Reads BenchmarkDotNet CSV format results from two directories and creates a rudimentary diff view.

DirectoryInfo oldDir, newDir;

switch (args)
{
    case [var oldDirArg, var newDirArg]:
        oldDir = FindDirectory(oldDirArg);
        newDir = FindDirectory(newDirArg);
        break;
    default:
        Console.Error.WriteLine("syntax: <old result path> <new result path>");
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

var writer = Console.Out;

foreach (var (i, oldFile, newFile) in CreateFilePairs(oldDir, newDir).Select((pair, i) => (i, pair.OldFile, pair.NewFile)))
{
    Console.Error.WriteLine("Analyzing pair " + oldFile.Name);

    if (i > 0)
    {
        writer.WriteLine();
        writer.WriteLine();
    }

    writer.WriteLine("## " + oldFile.Name.Replace("-report.csv", "", StringComparison.Ordinal));
    writer.WriteLine();

    var oldRows = File.ReadLines(oldFile.FullName).ParseCsv();
    var newRows = File.ReadLines(newFile.FullName).ParseCsv();

    using var rowPair = oldRows.Zip(newRows, (oldRow1, newRow1) => (Old: oldRow1, New: newRow1))
                               .GetEnumerator();

    if (!rowPair.MoveNext())
    {
        Console.Error.WriteLine("Incomplete data in one of the two files.");
        return 1;
    }

    var (oldRow, newRow) = rowPair.Current;

    var effectiveHeaders =
        ImmutableArray.CreateRange(
            from c in columns
            select (Name: c, Index: (Old: oldRow.FindFirstIndex(c, StringComparison.Ordinal),
                                     New: newRow.FindFirstIndex(c, StringComparison.Ordinal)))
            into c
            where c.Index is (not null, _) or (_, not null)
            select c);

    writer.WriteLine("| Diff |" + string.Join("|", from h in effectiveHeaders select h.Name) + "|");

    writer.Write("|------- ");
    foreach (var (name, _) in effectiveHeaders)
    {
        writer.Write("|-------");
        if (name.IndexOf("Gen ", StringComparison.OrdinalIgnoreCase) > -1 || name is "Allocated" or "Mean")
        {
            writer.Write(":");
        }
    }

    writer.WriteLine("|");

    var oldColumnValues = new string?[effectiveHeaders.Length];

    while (rowPair.MoveNext())
    {
        Array.Clear(oldColumnValues);

        (oldRow, newRow) = rowPair.Current;

        writer.Write("| Old |");
        foreach (var (hi, (_, (_, oldIndex))) in effectiveHeaders.Index())
        {
            var value = "-";
            if (oldIndex is { } someOldIndex)
                oldColumnValues[hi] = value = oldRow[someOldIndex];
            writer.Write(value + "|");
        }

        writer.WriteLine();

        writer.Write("| **New** |");
        foreach (var (hi, (name, (_, newIndex))) in effectiveHeaders.Index())
        {
            if (name is "Type" or "Method" or "N" or "FileName")
            {
                writer.Write("\t|");
            }
            else
            {
                var value = newIndex is { } someNewIndex ? newRow[someNewIndex] : "-";

                if (oldColumnValues[hi] is { } oldString)
                {
#pragma warning disable IDE0042 // Deconstruct variable declaration
                    var oldResult = SplitResult(oldString);
                    var newResult = SplitResult(value);
#pragma warning restore IDE0042 // Deconstruct variable declaration

                    if (string.IsNullOrWhiteSpace(oldResult.Unit) == string.IsNullOrWhiteSpace(newResult.Unit))
                    {
                        var canCalculateDiff = name is not "Error"
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
                            if (name is not "Error" && oldString != value)
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
}

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
