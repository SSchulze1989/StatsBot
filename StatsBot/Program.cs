// See https://aka.ms/new-console-template for more information
using iRLeagueApiCore.Client;
using iRLeagueApiCore.Client.Http;
using iRLeagueApiCore.Common.Models;
using iRLeagueApiCore.Common.Models.Standings;
using iRLeagueApiCore.Common.Models.Tracks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StatsBot;
using StatsBot.Properties;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var services = new ServiceCollection();
services.TryAddScoped(options => new HttpClient()
{
    BaseAddress = new Uri("https://irleaguemanager.net/api/")
});
services.AddScoped(configure =>
{
    var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    jsonOptions.Converters.Add(new JsonStringEnumConverter());
    jsonOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    return jsonOptions;
});
services.AddLeagueApiClient();

var provider = services.BuildServiceProvider();

// scan for argument
string? leagueName = default;
if (args.Length > 0)
{
    leagueName = args[0];
}
int i=1;
string outFolder = ".";
string outFile = string.Empty;
while (i < args.Length)
{
	switch (args[i])
	{
		case "--folder":			
			outFolder = args[++i];
			break;
        case "--file":
            outFile = args[++i];
            break;
	}
	i++;
}

var client = provider.GetRequiredService<ILeagueApiClient>();
if (string.IsNullOrWhiteSpace(leagueName))
{
    var leagues = (await client.Leagues().Get()).Content!;
    Console.WriteLine("Select league:");
    Console.WriteLine("   Id | Name");
    foreach(var selectLeague in leagues)
    {
        Console.WriteLine($"  {selectLeague.Id,3} | {selectLeague.Name}");
    }
    Console.Write("Select Id: ");
    var idString = Console.ReadLine();
    var id = int.TryParse(idString, out int res) ? res : throw new ArgumentException("Wrong Id");
    leagueName = leagues.First(x => x.Id == id)?.Name ?? throw new ArgumentException($"Id {id} does not exist");
    Console.WriteLine($"Selected {leagueName}");
}

var skipSeasons = leagueName.ToLower() switch
{
    "skippycup" => 9,
    "dac-f4-cup" => 3,
    _ => 0,
};
var importFile = leagueName.ToLower() switch
{
    "skippycup" => "AllTimeStats_SkippyCup_until_S21.csv",
    _ => string.Empty
};

client.SetCurrentLeague(leagueName);
var leagueRequestResult = await client.CurrentLeague!.Get();
if (leagueRequestResult.Success == false || leagueRequestResult.Content is null)
{
    Console.WriteLine($"No league with name {leagueName} found");
    return;
}
var league = leagueRequestResult.Content!;
if (string.IsNullOrEmpty(outFile))
{
    outFile = $"AllTimeStats_{league.Name}.csv";
}

var tracks = (await client.Tracks().Get()).Content!;
var members = (await client.CurrentLeague.Members().Get()).Content!;
var seasonIds = league.SeasonIds.Skip(skipSeasons);
var seasonData = seasonIds.Select(x => client.CurrentLeague.Seasons().WithId(x).Get().Result.Content!).ToList();
var seasonResults = seasonIds.Select(x => client.CurrentLeague.Seasons().WithId(x).Results().Get().Result.Content).Where(x => x is not null).ToList();
var seasonStandings = seasonIds.Select(x => client.CurrentLeague.Seasons().WithId(x).Standings().Get().Result.Content!).ToList();

IEnumerable<DriverStatisticRow> driverRows = Array.Empty<DriverStatisticRow>();
if (string.IsNullOrWhiteSpace(importFile) == false)
{
    var csvString = Resources.AllTimeStats_SkippyCup_until_S21;
    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csvString));
    var csvImportHelper = new CSVExportHelper();
    var importDriverRows = await csvImportHelper.ReadFromStream(stream);
    // clear member id because they are from the old api
    foreach(var row in importDriverRows)
    {
        row.MemberId = 0;
    }
    driverRows = importDriverRows.ToList();
}

foreach (var (season, results, standings) in seasonData.Zip(seasonResults, seasonStandings).OrderBy(x => x.First.SeasonEnd))
{
    if (results is null)
    {
        continue;
    }
    var seasonStats = CalculateSeasonStatistics(season, results, standings);
    Console.WriteLine("Calculated season stats");
    if (season.Finished)
    {
        foreach(var row in driverRows)
        {
            row.IsCurrentChamp = false;
            row.IsCurrentHeChamp = false;
        }
    }
    driverRows = AggregateStatisticRows(driverRows, seasonStats);
}

foreach(var row in driverRows)
{
    var rank = CalcDriverRank(row);
    row.DriverRank = rank?.ToString() ?? string.Empty;
    row.RankValue = (int)rank.GetValueOrDefault();
    row.FairPlayRating = CalculateFairplayRating(row);
}

Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

var csvFile = Path.Combine(outFolder, outFile);
using (var stream = new FileStream(csvFile, FileMode.Create, FileAccess.Write))
{
    var csvExportHelper = new CSVExportHelper();
    csvExportHelper.WriteToStream(stream, driverRows);
}

Console.WriteLine(league.Name);

IEnumerable<DriverStatisticRow> CalculateSeasonStatistics(SeasonModel season, IEnumerable<SeasonEventResultModel> seasonResults, IEnumerable<StandingsModel> seasonStandings)
{
    IEnumerable<DriverStatisticRow> driverStatRows = Array.Empty<DriverStatisticRow>();
    foreach(var seasonResult in seasonResults.OrderBy(x => x.EventResults.FirstOrDefault()?.Date))
    {
        var resultStatRows = CalculateEventStatistisc(seasonResult.EventResults);
        driverStatRows = AggregateStatisticRows(driverStatRows, resultStatRows);
    }

    // assign current season position
    var overallStandings = seasonStandings.First();
    var heStandings = seasonStandings.Skip(1).FirstOrDefault() ?? new();
    foreach (var row in driverStatRows)
    {
        var standingRow = overallStandings.StandingRows.FirstOrDefault(x => x.MemberId == row.MemberId);
        row.CurrentSeasonPosition = standingRow?.Position ?? 0;
    }

    // assign Titles
    if (season.Finished)
    {
        foreach(var row in driverStatRows)
        {
            if (row.CurrentSeasonPosition == 1)
            {
                row.Titles = 1;
                row.IsCurrentChamp = true;
            }
            else
            {
                row.IsCurrentChamp = false;
            }

            if (heStandings.StandingRows.FirstOrDefault(x => x.MemberId == row.MemberId)?.Position == 1)
            {
                row.HeTitles = 1;
                row.IsCurrentHeChamp = true;
            }
            else {
                row.IsCurrentHeChamp = false;
            }
        }
    }

    return driverStatRows;
}

IEnumerable<DriverStatisticRow> CalculateEventStatistisc(IEnumerable<EventResultModel> eventResult)
{
    if (eventResult.Any() == false || eventResult.First().SessionResults.Any() == false)
    {
        return Array.Empty<DriverStatisticRow>();
    }

    var rows = new List<DriverStatisticRow>();
    // get overall combined event results
    var overall = eventResult.First();
    var combined = overall.SessionResults.Last();
    var track = tracks!.SelectMany(x => x.Configs).First(x => x.TrackId == overall.TrackId);

    // get statistic rows from results
    var statRows = CalculateResultStatistics(overall, combined, track, combined.ResultRows);
    return statRows;
}

IEnumerable<DriverStatisticRow> CalculateResultStatistics(EventResultModel eventResult, ResultModel sessionResult, TrackConfigModel track, IEnumerable<ResultRowModel> resultRows)
{
    var statRows = new List<DriverStatisticRow>();
    foreach(var row in resultRows)
    {
        var statRow = new DriverStatisticRow()
        {
            Name = $"{row.Firstname} {row.Lastname}",
            IRacingId = FindMemberIracingId(members, row.MemberId.GetValueOrDefault()),
            TeamName = row.TeamName,
            AvgFinalPosition = row.FinishPosition,
            AvgFinishPosition = row.FinishPosition,
            AvgIncidentsPerKm = row.CompletedLaps > 0 ? row.Incidents / track.Length*row.CompletedLaps : 0,
            AvgIncidentsPerLap = row.CompletedLaps > 0 ? row.Incidents / row.CompletedLaps : 0,
            AvgIncidentsPerRace = row.Incidents,
            AvgIRating = row.NewIrating,
            AvgPenaltyPointsPerKm = row.CompletedLaps > 0 ? row.PenaltyPoints / track.Length * row.CompletedLaps : 0,
            AvgPenaltyPointsPerLap = row.CompletedLaps > 0 ? row.PenaltyPoints / row.CompletedLaps : 0,
            AvgPenaltyPointsPerRace = row.PenaltyPoints,
            AvgPointsPerRace = row.RacePoints,
            AvgSRating = row.NewSafetyRating / 100.0,
            AvgStartPosition = row.StartPosition,
            BestFinalPosition = row.FinalPosition,
            BestFinishPosition = (int)row.FinishPosition,
            BestStartPosition = (int)row.StartPosition,
            BonusPoints = (int)row.BonusPoints,
            CompletedLaps = (int)row.CompletedLaps,
            DrivenKm = row.CompletedLaps * track.Length,
            EndIRating = row.NewIrating,
            EndSRating = row.NewSafetyRating / 100.0,
            FirstRaceDate = eventResult.Date,
            FastestLaps = sessionResult.ResultRows.Select(x => x.FastestLapTime).Where(x => x > TimeSpan.Zero).Min() == row.FastestLapTime ? 1 : 0,
            FirstRaceFinalPosition = (int)row.FinalPosition,
            FirstRaceFinishPosition = (int)row.FinishPosition,
            FirstRaceStartPosition = (int)row.StartPosition,
            FirstSessionDate = eventResult.Date,
            LastRaceFinalPosition = (int)row.FinalPosition,
            LastRaceDate = eventResult.Date,
            LastRaceFinishPosition = (int)row.FinishPosition,
            LastRaceStartPosition = (int)row.StartPosition,
            LastSessionDate = eventResult.Date,
            LeadingKm = row.LeadLaps * track.Length,
            LeadingLaps = (int)row.LeadLaps,
            Incidents = (int)row.Incidents,
            MemberId = row.MemberId.GetValueOrDefault(),
            MemberName = $"{row.Firstname} {row.Lastname}",
            PenaltyPoints = (int)row.PenaltyPoints,
            Poles = row.FinalPosition == 1 ? 1 : 0,
            RacePoints = (int)row.RacePoints,
            Races = 1,
            RacesCompleted = row.CompletedPct > 0.75 ? 1 : 0,
            RacesCompletedPctVal = row.CompletedPct > 0.75 ? 1 : 0,
            RacesInPoints = row.RacePoints > 0 ? 1 : 0,
            StartIRating = row.OldIrating,
            StartSRating = row.OldSafetyRating / 100.0,
            Top10 = row.FinalPosition <= 10 ? 1 : 0,
            Top15 = row.FinalPosition <= 15 ? 1 : 0,
            Top20 = row.FinalPosition <= 20 ? 1 : 0,
            Top25 = row.FinalPosition <= 25 ? 1 : 0,
            Top3 = row.FinalPosition <= 3 ? 1 : 0,
            Top5 = row.FinalPosition <= 5 ? 1 : 0,
            TotalPoints = (int)row.TotalPoints,
            Wins = row.FinalPosition == 1 ? 1 : 0,
            WorstFinalPosition = row.FinalPosition,
            WorstFinishPosition = (int)row.FinishPosition,
            WorstStartPosition = (int)row.StartPosition,
        };
        statRows.Add(statRow);
    }
    return statRows;
}

IEnumerable<DriverStatisticRow> AggregateStatisticRows(IEnumerable<DriverStatisticRow> first, IEnumerable<DriverStatisticRow> second)
{
    var countFirst = first.Where(x => x.MemberId != 0);
    var countSecond = second.Where(x => x.MemberId != 0);
    if (countFirst.DistinctBy(x => x.MemberId).Count() != countFirst.Count() || countSecond.DistinctBy(x => x.MemberId).Count() != countSecond.Count())
    {
        throw new InvalidOperationException("Member ids in statistic rows not unique!");
    }

    var returnRows = first.ToList();
    foreach(var secondRow in second)
    {
        var firstRow = first.FirstOrDefault(x => x.MemberId == secondRow.MemberId && x.MemberId != 0)
            ?? first.FirstOrDefault(x => x.IRacingId == secondRow.IRacingId && string.IsNullOrWhiteSpace(x.IRacingId) == false)
            ?? first.FirstOrDefault(x => x.Name == secondRow.Name);
        if (firstRow == null)
        {
            returnRows.Add(secondRow);
            continue;
        }

        firstRow.IRacingId = string.IsNullOrEmpty(firstRow.IRacingId) ? secondRow.IRacingId : firstRow.IRacingId;
        firstRow.AvgFinalPosition = CalcAvg(firstRow, secondRow, x => x.AvgFinalPosition, x => x.Races);
        firstRow.AvgFinishPosition = CalcAvg(firstRow, secondRow, x => x.AvgFinishPosition, x => x.Races);
        firstRow.AvgIncidentsPerKm = CalcAvg(firstRow, secondRow, x => x.AvgIncidentsPerKm, x => x.DrivenKm);
        firstRow.AvgIncidentsPerLap = CalcAvg(firstRow, secondRow, x => x.AvgIncidentsPerLap, x => x.CompletedLaps);
        firstRow.AvgIncidentsPerRace = CalcAvg(firstRow, secondRow, x => x.AvgIncidentsPerRace, x => x.Races);
        firstRow.AvgIRating = CalcAvg(firstRow, secondRow, x => x.AvgIRating, x => x.Races);
        firstRow.AvgPenaltyPointsPerKm = CalcAvg(firstRow, secondRow, x => x.AvgPenaltyPointsPerKm, x => x.DrivenKm);
        firstRow.AvgPenaltyPointsPerLap = CalcAvg(firstRow, secondRow, x => x.AvgPenaltyPointsPerLap, x => x.CompletedLaps);
        firstRow.AvgPenaltyPointsPerRace = CalcAvg(firstRow, secondRow, x => x.AvgPenaltyPointsPerRace, x => x.Races);
        firstRow.AvgPointsPerRace = CalcAvg(firstRow, secondRow, x => x.AvgPointsPerRace, x => x.Races);
        firstRow.AvgSRating = CalcAvg(firstRow, secondRow, x => x.AvgSRating, x => x.Races);
        firstRow.AvgStartPosition = CalcAvg(firstRow, secondRow, x => x.AvgStartPosition, x => x.Races);
        firstRow.BestFinalPosition = Math.Min(firstRow.BestFinalPosition, secondRow.BestFinalPosition);
        firstRow.BestFinishPosition = Math.Min(firstRow.BestFinishPosition, secondRow.BestFinishPosition);
        firstRow.BestStartPosition = Math.Min(firstRow.BestStartPosition, secondRow.BestStartPosition);
        firstRow.BonusPoints += secondRow.BonusPoints;
        firstRow.CompletedLaps += secondRow.CompletedLaps;
        firstRow.DrivenKm += secondRow.DrivenKm;
        firstRow.EndIRating = secondRow.EndIRating;
        firstRow.EndSRating = secondRow.EndSRating;
        firstRow.FastestLaps += secondRow.FastestLaps;
        firstRow.LastRaceFinalPosition = secondRow.LastRaceFinalPosition;
        firstRow.LastRaceDate = secondRow.LastRaceDate;
        firstRow.LastRaceFinishPosition = secondRow.LastRaceFinishPosition;
        firstRow.LastRaceStartPosition = secondRow.LastRaceStartPosition;
        firstRow.LastSessionDate = secondRow.LastSessionDate;
        firstRow.LeadingKm += secondRow.LeadingKm;
        firstRow.LeadingLaps += secondRow.LeadingLaps;
        firstRow.Incidents += secondRow.Incidents;
        firstRow.PenaltyPoints += secondRow.PenaltyPoints;
        firstRow.Poles += secondRow.Poles;
        firstRow.RacePoints += secondRow.RacePoints;
        firstRow.Races += secondRow.Races;
        firstRow.RacesCompleted += secondRow.RacesCompleted;
        firstRow.RacesCompletedPctVal = CalcAvg(firstRow, secondRow, x => x.RacesCompletedPctVal, x => x.Races);
        firstRow.RacesInPoints += secondRow.RacesInPoints;
        firstRow.TeamName = secondRow.TeamName;
        firstRow.Top10 += secondRow.Top10;
        firstRow.Top15 += secondRow.Top15;
        firstRow.Top20 += secondRow.Top20;
        firstRow.Top25 += secondRow.Top25;
        firstRow.Top3 += secondRow.Top3;
        firstRow.Top5 += secondRow.Top5;
        firstRow.TotalPoints += secondRow.TotalPoints;
        firstRow.Wins += secondRow.Wins;
        firstRow.WorstFinalPosition = Math.Max(firstRow.WorstFinalPosition, secondRow.WorstFinalPosition);
        firstRow.WorstFinishPosition = Math.Max(firstRow.WorstFinishPosition, secondRow.WorstFinishPosition);
        firstRow.WorstStartPosition = Math.Max(firstRow.WorstStartPosition, secondRow.WorstStartPosition);
        firstRow.Titles += secondRow.Titles;
        firstRow.HeTitles += secondRow.HeTitles;
        firstRow.IsCurrentChamp |= secondRow.IsCurrentChamp;
        firstRow.IsCurrentHeChamp |= secondRow.IsCurrentHeChamp;
    }

    return returnRows;
}

static double CalcAvg<T>(T first, T second, Func<T, double> value, Func<T, double> weight)
{
    var totalWeight = weight(first) + weight(second);
    if (totalWeight == 0)
    {
        return 0;
    }
    return (value(first)*weight(first) + value(second)*weight(second)) / totalWeight;
}

static DriverRank? CalcDriverRank(DriverStatisticRow row)
{
    if (row.Races == 0)
        return null;

    DriverRank? rank = null;

    if (row.IsCurrentChamp)
    {
        rank = DriverRank.Meister;
    }
    else if (row.IsCurrentHeChamp)
    {
        rank = DriverRank.EisenMeister;
    }
    else if (row.RacesCompleted >= 30 && row.Titles >= 1)
    {
        rank = DriverRank.Platin;
    }
    else if (row.HeTitles >= 1)
    {
        rank = DriverRank.Eisen;
    }
    else if (row.RacesCompleted >= 30 && row.Wins >= 5)
    {
        rank = DriverRank.GoldPlus;
    }
    else if (row.RacesCompleted >= 20 && row.Wins >= 1)
    {
        rank = DriverRank.Gold;
    }
    else if (row.RacesCompleted >= 20 && row.Top3 >= 5)
    {
        rank = DriverRank.SilberPlus;
    }
    else if (row.RacesCompleted >= 10 && (row.Top3 >= 1 || row.Top10 >= 10))
    {
        rank = DriverRank.Silber;
    }
    else if (row.RacesCompleted >= 10 && row.Top10 >= 5)
    {
        rank = DriverRank.BronzePlus;
    }
    else if (row.RacesCompleted >= 5 && (row.Top10 >= 1 || row.RacesCompleted >= 25))
    {
        rank = DriverRank.Bronze;
    }

    return rank;
}

static double CalculateFairplayRating(DriverStatisticRow row)
{
    var minRacesToGetRating = 6;
    return (row.RacesCompleted >= minRacesToGetRating) ? (double)Math.Round(((row.PenaltyPoints + (double)row.Incidents / 15) / row.RacesCompleted), 4) : 100;
}

static string FindMemberIracingId(IEnumerable<MemberModel> memberRows, long memberId)
{
    foreach(var row in memberRows)
    {
        if (row.MemberId == memberId)
        {
            return row.IRacingId;
        }
    }
    return string.Empty;
}

enum DriverRank
{
    [Description("")]
    None = 0,
    Bronze = 1,
    BronzePlus = 2,
    Silber = 3,
    SilberPlus = 4,
    Gold = 5,
    GoldPlus = 6,
    Eisen = 7,
    Platin = 9,
    EisenMeister = 8,
    Meister = 10
}