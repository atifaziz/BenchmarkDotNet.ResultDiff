using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using Dsv;
#if !NET9_0_OR_GREATER
using static MoreLinq.Extensions.IndexExtension;
#endif
using static MoreLinq.Extensions.ToDelimitedStringExtension;
using MoreEnumerable = MoreLinq.MoreEnumerable;

#pragma warning disable CA2201 // Do not raise reserved exception types

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

ImmutableArray<string> columns =
[
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
];

var writer = Console.Out;

foreach (var (i, (oldFile, newFile)) in CreateFilePairs(oldDir, newDir).Index())
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

    var table = FormatTable(oldRows.Zip(newRows)).ToList();

    var widths = table.Select(row => from c in row select c.Text.Length)
                      .Aggregate((acc, row) => from e in acc.Zip(row) select Math.Max(e.First, e.Second))
                      .ToImmutableArray();

    var dashes = new string('-', widths.Max());

    foreach (var (j, row) in table.Index())
    {
        writer.WriteLine($"| {row.Zip(widths, (c, w) => c.Alignment is Alignment.Right ? c.Text.PadLeft(w) : c.Text.PadRight(w))
                                 .ToDelimitedString(" | ")} |");

        if (j is 0)
        {
            var rs = from c in row select c.Alignment is Alignment.Right ? ':' : ' ';
            var ds = from w in widths select dashes[..w];
            writer.WriteLine($"|{ds.Zip(rs, (d, r) => $" {d}{r}").ToDelimitedString("|")}|");
        }
    }
}

IEnumerable<Cell[]> FormatTable(IEnumerable<(TextRow Old, TextRow New)> pairedRows)
{
    using var rowPair = pairedRows.GetEnumerator();

    if (!rowPair.MoveNext())
        throw new("Incomplete data in one of the two files.");

    var (oldRow, newRow) = rowPair.Current;

    var effectiveHeaders =
        ImmutableArray.CreateRange(
            from c in columns
            select new
            {
                Name      = c,
                Index     = (Old: oldRow.FindFirstIndex(c, StringComparison.Ordinal),
                             New: newRow.FindFirstIndex(c, StringComparison.Ordinal)),
                Alignment = c.IndexOf("Gen ", StringComparison.OrdinalIgnoreCase) > -1 || c is "Allocated" or "Mean" or "Error"
                          ? Alignment.Right
                          : Alignment.Left
            }
    into c
    where c.Index is (not null, _) or (_, not null)
            select c);

    yield return MoreEnumerable.Return(new Cell("Diff"))
                               .Concat(from h in effectiveHeaders select new Cell(h.Name, h.Alignment))
                               .ToArray();

    var oldColumnValues = new string?[effectiveHeaders.Length];

    while (rowPair.MoveNext())
    {
        Array.Clear(oldColumnValues);

        (oldRow, newRow) = rowPair.Current;

        var cells = new Cell[effectiveHeaders.Length + 1];
        cells[0] = new Cell("Old");
        foreach (var (hi, h) in effectiveHeaders.Index())
        {
            var value = "-";
            if (h.Index.Old is { } someOldIndex)
                oldColumnValues[hi] = value = oldRow[someOldIndex];
            cells[hi + 1] = new(h.Name is "Type" or "Method" ? $"`{value}`" : value, h.Alignment);
        }

        yield return cells;

        cells = new Cell[effectiveHeaders.Length + 1];
        cells[0] = new Cell("**New**");

        foreach (var (hi, h) in effectiveHeaders.Index())
        {
            if (h.Name is "Type" or "Method" or "N" or "FileName")
            {
                cells[hi + 1] = new(string.Empty);
            }
            else
            {
                var value = h.Index.New is { } someNewIndex ? newRow[someNewIndex] : "-";

                if (oldColumnValues[hi] is { } oldString)
                {
#pragma warning disable IDE0042 // Deconstruct variable declaration
                    var oldResult = SplitResult(oldString);
                    var newResult = SplitResult(value);
#pragma warning restore IDE0042 // Deconstruct variable declaration

                    if (string.IsNullOrWhiteSpace(oldResult.Unit) == string.IsNullOrWhiteSpace(newResult.Unit))
                    {
                        var canCalculateDiff = h.Name is not "Error"
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
                            if (h.Name is not "Error" && oldString != value)
                            {
                                Console.Error.WriteLine("Cannot calculate diff for " + oldString + " vs " + value);
                            }
                        }
                    }
                }

                cells[hi + 1] = new Cell($"**{value}**", h.Alignment);
            }
        }

        yield return cells;
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

enum Alignment { Left, Center, Right }
sealed record Cell(string Text, Alignment Alignment = Alignment.Left);
