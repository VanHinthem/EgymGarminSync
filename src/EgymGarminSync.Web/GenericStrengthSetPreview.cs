using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace EgymGarminSync.Web;

/// <summary>
/// Offline-only EGYM -> Garmin strength set planning. No mutation, no synthetic timestamps,
/// no generated PUT payload. Keeps the existing correction/write path entirely untouched.
/// </summary>
internal static class GenericStrengthSetPreview
{
    private static readonly IReadOnlyDictionary<long,(string Label,string Category,string? Name)> Mappings =
        new Dictionary<long,(string,string,string?)>
        {
            [333]=("Chest Press","BENCH_PRESS",null),
            [334]=("Seated Row","ROW","SEATED_CABLE_ROW"),
            [581]=("Leg Press","SQUAT","LEG_PRESS"),
            [330]=("Abdominal Crunch","CRUNCH","WEIGHTED_THORACIC_CRUNCHES_ON_FOAM_ROLLER"),
            [335]=("Lat Pulldown","PULL_UP","LAT_PULLDOWN"),
            [329]=("Leg Extension","CRUNCH","WEIGHTED_LEG_EXTENSIONS"),
            [331]=("Back Extension","HYPEREXTENSION","WEIGHTED_STATIC_BACK_EXTENSION"),
            [332]=("Leg Curl","LEG_CURL","WEIGHTED_LEG_CURL")
        };

    internal static async Task<IResult> GetAsync(string egymId,long garminId,SyncDbContext db,HttpContext http)
    {
        if(http.Connection.RemoteIpAddress is not {} ip || !System.Net.IPAddress.IsLoopback(ip))
            return Results.StatusCode(403);
        if(string.IsNullOrWhiteSpace(egymId)||garminId<=0)
            return Results.BadRequest(new {error="Specify EGYM session and Garmin activity."});
        var workout=await db.Sessions.AsNoTracking().SingleOrDefaultAsync(
            x=>x.Source=="EGYM"&&x.SourceId==egymId);
        var activity=await db.Sessions.AsNoTracking().SingleOrDefaultAsync(
            x=>x.Source=="Garmin"&&x.SourceId==garminId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var cached=await db.GarminSets.AsNoTracking().SingleOrDefaultAsync(x=>x.GarminActivityId==garminId);
        if(workout is null||activity is null||cached is null)
            return Results.NotFound(new {error="Both cached sessions and Garmin exercise sets are required."});

        using var egymDoc=JsonDocument.Parse(workout.SourceJson);
        using var garminDoc=JsonDocument.Parse(cached.ExerciseSetsJson);
        var source=egymDoc.RootElement;
        var target=garminDoc.RootElement;
        if(!source.TryGetProperty("exercises",out var exercises)||exercises.ValueKind!=JsonValueKind.Array||
           !target.TryGetProperty("exerciseSets",out var existing)||existing.ValueKind!=JsonValueKind.Array)
            return Results.UnprocessableEntity(new {error="Unsupported source or Garmin cache shape."});

        var proposal=new List<object>();
        var warnings=new List<string>();
        foreach(var exercise in exercises.EnumerateArray())
        {
            if(!exercise.TryGetProperty("activity",out var refNode)||
               !refNode.TryGetProperty("activityId",out var idNode)||!idNode.TryGetInt64(out var id))
            {
                warnings.Add("EGYM exercise without usable identity.");
                continue;
            }
            if(id==31)continue; // Cycling is not a strength set.
            if(!Mappings.TryGetValue(id,out var map))
            {
                warnings.Add($"Unmapped EGYM exercise ID {id}.");
                continue;
            }
            if(!exercise.TryGetProperty("sets",out var sets)||sets.ValueKind!=JsonValueKind.Array||sets.GetArrayLength()!=2)
            {
                warnings.Add($"{map.Label}: expected two circuit rounds.");
                continue;
            }
            var index=0;
            foreach(var set in sets.EnumerateArray())
            {
                index++;
                if(!set.TryGetProperty("weight",out var kg)||!kg.TryGetDecimal(out var weight)||
                   !set.TryGetProperty("numberOfReps",out var rep)||!rep.TryGetInt32(out var reps)||
                   weight<0||reps<0)
                {
                    warnings.Add($"{map.Label}: round {index} has invalid load/repetitions.");
                    continue;
                }
                proposal.Add(new {egymExerciseId=id,exercise=map.Label,round=index,
                    garminCategory=map.Category,garminName=map.Name,weightKg=weight,repetitions=reps,
                    startTime=(string?)null,durationSeconds=(int?)null});
            }
        }
        var existingItems=existing.EnumerateArray().ToArray();
        var active=existingItems.Count(x=>x.TryGetProperty("setType",out var type)&&type.ValueKind==JsonValueKind.String&&type.GetString()=="ACTIVE");
        var rest=existingItems.Count(x=>x.TryGetProperty("setType",out var type)&&type.ValueKind==JsonValueKind.String&&type.GetString()=="REST");
        return Results.Ok(new {readOnly=true,writeAllowed=false,egymId,garminId,
            existingGarminSets=new {total=existingItems.Length,active,rest},
            proposedStrengthSets=proposal,proposedCount=proposal.Count,
            warnings,
            timing="Unresolved: EGYM sets have no verified Garmin startTime/duration mapping.",
            payloadGenerated=false,
            note="Offline mapping only; not a valid Garmin write request. Existing activity data is unchanged."});
    }
}
