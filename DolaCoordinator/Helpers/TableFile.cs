using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ClosedXML.Excel;

namespace DolaCoordinator.Helpers;

/// <summary>
/// Đọc dữ liệu dạng bảng từ Excel (.xlsx) hoặc CSV/TSV thành các dòng chuỗi (dòng đầu là tiêu đề).
/// Ô Excel nhiều dòng giữ nguyên xuống dòng; đọc được cả khi file đang mở trong Excel.
/// </summary>
public static class TableFile
{
    public const string HelpSheetName = "Hướng dẫn";

    public static bool IsExcel(string path) => Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase);

    public static List<string[]> ReadRows(string path)
    {
        if (IsExcel(path)) return ReadXlsx(path);
        var text = File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿');
        return PromptFileParser.ReadCsv(text);
    }

    private static List<string[]> ReadXlsx(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var wb = new XLWorkbook(stream);

        // Sheet đầu tiên có dữ liệu, bỏ qua sheet hướng dẫn
        var ws = wb.Worksheets.FirstOrDefault(w => !w.Name.Equals(HelpSheetName, StringComparison.OrdinalIgnoreCase) && w.LastCellUsed() != null)
                 ?? throw new InvalidDataException("File Excel không có sheet nào chứa dữ liệu.");

        var lastRow = ws.LastRowUsed()!.RowNumber();
        var lastCol = ws.LastColumnUsed()!.ColumnNumber();
        var rows = new List<string[]>();
        for (var r = 1; r <= lastRow; r++)
        {
            var cells = new string[lastCol];
            var any = false;
            for (var c = 1; c <= lastCol; c++)
            {
                var cell = ws.Cell(r, c);
                var v = cell.IsEmpty() ? string.Empty : CellText(cell);
                cells[c - 1] = v;
                if (v.Length > 0) any = true;
            }
            if (any) rows.Add(cells);
        }
        return rows;
    }

    private static string CellText(IXLCell cell)
    {
        // Số (vd. thời lượng 30, mật khẩu toàn số) → chuỗi không có phần thập phân thừa
        if (cell.DataType == XLDataType.Number)
        {
            var d = cell.GetDouble();
            return d == Math.Floor(d) ? ((long)d).ToString(CultureInfo.InvariantCulture) : d.ToString(CultureInfo.InvariantCulture);
        }
        return cell.GetString().Replace("\r\n", "\n").Replace("\r", "\n").Trim('\n', ' ', '\t').Replace("\n", Environment.NewLine);
    }
}
