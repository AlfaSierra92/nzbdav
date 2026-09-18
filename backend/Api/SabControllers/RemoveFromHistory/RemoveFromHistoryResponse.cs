using System.Text.Json.Serialization;
﻿namespace NzbWebDAV.Api.SabControllers.RemoveFromHistory;

public class RemoveFromHistoryResponse : SabBaseResponse
{
    [JsonPropertyName("removedIds")]
    public List<Guid> RemovedIds { get; init; } = [];
}