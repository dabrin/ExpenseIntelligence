using ExpenseIntelligence.Application;
using ExpenseIntelligence.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ExpenseIntelligence.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddExpenseIntelligence(this IServiceCollection s, IConfiguration config)
    {
        s.Configure<StorageOptions>(config.GetSection(StorageOptions.Section));
        s.AddSingleton(TimeProvider.System);
        s.AddSingleton<JsonExpenseStore>();
        s.AddSingleton<IExpenseStore>(sp => sp.GetRequiredService<JsonExpenseStore>());
        s.AddSingleton<ImportService>();
        s.AddSingleton<ExpenseQueryService>();
        s.AddSingleton<ExpenseService>();
        s.AddSingleton<AnalyticsService>();
        s.AddSingleton<HistoryService>();
        s.AddSingleton<Export.ExportService>();
        s.AddSingleton(config.GetSection(BulkOptions.Section).Get<BulkOptions>() ?? new BulkOptions());
        s.AddSingleton<BulkService>();
        s.AddSingleton<DatasetService>();
        s.AddSingleton<InsightsService>();
        s.AddSingleton<DataDoctorService>();
        s.AddSingleton<AssistantService>();
        return s;
    }
}
