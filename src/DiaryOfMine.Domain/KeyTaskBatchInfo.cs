using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiaryOfMine.Domain;

public sealed record KeyTaskBatchInfo(
    Guid BatchId,
    DateOnly StartDate,
    DateOnly EndDate,
    int WeekdaysMask,
    int StartMinute,
    int EndMinute)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }

    public KeyTaskBatchInfo Validate()
    {
        if (BatchId == Guid.Empty)
            throw new ArgumentException("Task 批次标识不能为空。", nameof(BatchId));
        if (EndDate < StartDate)
            throw new ArgumentException("Task 结束日期不能早于开始日期。", nameof(EndDate));
        if (WeekdaysMask is <= 0 or > 0x7F)
            throw new ArgumentException("Task 星期规则无效。", nameof(WeekdaysMask));
        if (StartMinute is < 0 or >= 1440)
            throw new ArgumentException("Task 开始时间无效。", nameof(StartMinute));
        if (EndMinute is <= 0 or > 1440 || EndMinute <= StartMinute)
            throw new ArgumentException("Task 结束时间无效。", nameof(EndMinute));
        return this;
    }
}
