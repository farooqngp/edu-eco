using System.Data;
using Dapper;

namespace EduEco.Infrastructure.Persistence.Dapper;

/// <summary>Process-wide Dapper settings; safe to call repeatedly.</summary>
public static class DapperConfiguration
{
    private static int _configured;

    public static void Configure()
    {
        if (Interlocked.Exchange(ref _configured, 1) == 1)
        {
            return;
        }

        SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
        SqlMapper.AddTypeHandler(new TimeOnlyTypeHandler());
    }

    private sealed class DateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override void SetValue(IDbDataParameter parameter, DateOnly value)
        {
            parameter.DbType = DbType.Date;
            parameter.Value = value.ToDateTime(TimeOnly.MinValue);
        }

        public override DateOnly Parse(object value) => value switch
        {
            DateOnly date => date,
            DateTime dateTime => DateOnly.FromDateTime(dateTime),
            _ => throw new DataException($"Cannot convert {value.GetType().Name} to DateOnly."),
        };
    }

    private sealed class TimeOnlyTypeHandler : SqlMapper.TypeHandler<TimeOnly>
    {
        public override void SetValue(IDbDataParameter parameter, TimeOnly value)
        {
            parameter.DbType = DbType.Time;
            parameter.Value = value.ToTimeSpan();
        }

        public override TimeOnly Parse(object value) => value switch
        {
            TimeOnly time => time,
            TimeSpan span => TimeOnly.FromTimeSpan(span),
            _ => throw new DataException($"Cannot convert {value.GetType().Name} to TimeOnly."),
        };
    }
}
