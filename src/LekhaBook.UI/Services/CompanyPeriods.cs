using LekhaBook.Domain;

namespace LekhaBook.UI.Services;

/// <summary>The company's today and fiscal year, which report periods count from (D12).</summary>
public sealed record CompanyPeriods(DateOnly Today, int FiscalYearStartMonth)
{
    /// <summary>Falls back to Nepal's time zone and Shrawan if the company's details can't be read.</summary>
    public static async Task<CompanyPeriods> LoadAsync(ICompanyApi companies, Guid companyId)
    {
        var details = (await companies.GetCompanyAsync(companyId)).Value;
        var zone = details?.TimeZoneId ?? CompanyClock.DefaultTimeZoneId;
        return new CompanyPeriods(CompanyClock.Today(zone, TimeProvider.System), details?.FiscalYearStartMonth ?? 4);
    }
}
