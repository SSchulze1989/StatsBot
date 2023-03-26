using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;

namespace StatsBot;

public sealed class CSVExportHelper
{
    public char Delimiter { get; set; } = ';';
    public bool UseAttributeNames { get; set; }
    public CultureInfo Culture { get; set; } = CultureInfo.InvariantCulture;

    private static readonly Dictionary<Type, Func<object?, string>> typeConversions;
    public static ReadOnlyDictionary<Type, Func<object?, string>> TypeConversions => new(typeConversions);

    static CSVExportHelper()
    {
        typeConversions = new Dictionary<Type, Func<object?, string>>
            {
                { typeof(double), x => ((double?)x)?.ToString("0.00") ?? "0.00" },
                { typeof(DateTime), x => ((DateTime?)x)?.ToString(@"MM.dd.yyyy") ?? "" },
                { typeof(DateTime?), x => ((DateTime?)x)?.ToString(@"MM.dd.yyyy") ?? "" }
            };
    }

    private static string ColumnValueToString(Type type, string columnName, object? value)
    {
        if (type != null && TypeConversions.ContainsKey(type))
        {
            var conversion = TypeConversions[type];
            return conversion.Invoke(value);
        }
        else
        {
            return value?.ToString() ?? "";
        }
    }

    private string GetRow(IEnumerable<KeyValuePair<string, PropertyInfo>> columns, object rowItem)
    {
        var columnsData = columns.Select(x => ColumnValueToString(x.Value.PropertyType, x.Key, x.Value.GetValue(rowItem)));
        return columnsData.Aggregate((x, y) => x + Delimiter + y);
    }

    public void WriteToStream(Stream stream, IEnumerable<object> data)
    {
        Thread.CurrentThread.CurrentCulture = Culture;

        var stringBuilder = new StringBuilder();

        // get column properties
        var columns = GetCSVColumns(data.First());

        // write headers
        stringBuilder.AppendLine(columns.Select(x => x.Key).Aggregate((x, y) => x + Delimiter + y));

        // write data rows
        foreach (var item in data)
        {
            stringBuilder.AppendLine(GetRow(columns, item));
        }

        // write data as binary to stream writer
        var writer = new BinaryWriter(stream);
        byte[] bytes = Encoding.UTF8.GetBytes(stringBuilder.ToString());
        writer.Write(bytes, 0, bytes.Length);
    }

    public async Task<IEnumerable<DriverStatisticRow>> ReadFromStream(Stream stream)
    {
        Thread.CurrentThread.CurrentCulture = Culture;

        var columns = GetCSVColumns(typeof(DriverStatisticRow));

        using var reader = new StreamReader(stream);

        var line = await reader.ReadLineAsync()
            ?? throw new InvalidOperationException("CSV does not contain header");
        var headers = line.Split(Delimiter).ToList();

        List<DriverStatisticRow> driverStatisticRows = new();
        while(reader.EndOfStream == false)
        {
            line = await reader.ReadLineAsync()
                ?? throw new InvalidOperationException("Unexpected end of csv");
            var csvColumns = line.Split(Delimiter);
            var row = GetRowFromCSVColumns(columns, headers.Zip(csvColumns).Select(x => new KeyValuePair<string, string>(x.First, x.Second)));
            driverStatisticRows.Add(row);
        }
        return driverStatisticRows;
    }

    private DriverStatisticRow GetRowFromCSVColumns(IEnumerable<KeyValuePair<string, PropertyInfo>> columns, IEnumerable<KeyValuePair<string, string>> csvValues)
    {
        var row = new DriverStatisticRow();
        foreach(var columnPair in columns)
        {
            var valuePair = csvValues.FirstOrDefault(x => x.Key == columnPair.Key);
            if (valuePair.Key != columnPair.Key)
            {
                continue;
            }
            var column = columnPair.Value;
            var value = valuePair.Value;
            Type columnType = column.PropertyType;
            try
            {
                // get column type and convert csv value string
                // if target is nullable
                var nullableType = Nullable.GetUnderlyingType(columnType);
                if (nullableType != null)
                {
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }
                    columnType = nullableType;
                }
                // set value on row
                if (columnType.IsEnum)
                {
                    column.SetValue(row, Enum.Parse(columnType, value));
                    continue;
                }

                var columnValue = Convert.ChangeType(value, columnType);
                column.SetValue(row, columnValue);
            }
            catch (Exception ex) when (
                ex is ArgumentException ||
                ex is ArgumentNullException ||
                ex is OverflowException ||
                ex is InvalidCastException ||
                ex is FormatException)
            {
                throw new InvalidOperationException($"Converting csv column value to row value failed on column: {columnPair.Key}|{valuePair.Key}: \"{value}\" -> {columnType}");
            }
        }
        return row;
    }

    private IEnumerable<KeyValuePair<string, PropertyInfo>> GetCSVColumns<T>(T item) where T : notnull
    {
        return GetCSVColumns(item.GetType());
    }

    private IEnumerable<KeyValuePair<string, PropertyInfo>> GetCSVColumns(Type itemType)
    {
        var properties = itemType
            .GetProperties();

        List<KeyValuePair<string, PropertyInfo>> columns = new List<KeyValuePair<string, PropertyInfo>>();
        foreach (var property in properties)
        {
            var dataMemberAttribute = (DataMemberAttribute?)property.GetCustomAttribute(typeof(DataMemberAttribute));
            string columnName;
            if (dataMemberAttribute is not null && dataMemberAttribute.IsNameSetExplicitly && dataMemberAttribute.Name is not null)
            {
                columnName = dataMemberAttribute.Name;
            }
            else
            {
                columnName = property.Name;
            }
            columns.Add(new KeyValuePair<string, PropertyInfo>(columnName, property));
        }
        return columns;
    }
}
