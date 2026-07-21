using Google.Protobuf;
using MutualGPU.Protocol;

namespace MutualGPU.Protocol.Tests;

public sealed class ProviderEnvelopeTests
{
    [Fact]
    public void TypeScript_and_browser_protobuf_fixtures_are_canonical_dotnet_messages()
    {
        var connect = ProviderMessage.Parser.ParseFrom(Convert.FromBase64String("CgcIARoDa2V5"));
        var resultUpload = ProviderMessage.Parser.ParseFrom(Convert.FromBase64String("MhcKBmhhbmRsZRIEdGFzaxoHYXR0ZW1wdA=="));
        var assignment = ServerMessage.Parser.ParseFrom(Convert.FromBase64String("ElMKBHRhc2sSB2F0dGVtcHQaBmhhbmRsZSIKCgRzZWVkEgI0MiouChdodHRwczovL2lucHV0LmV4YW1wbGUvYRIJaW1hZ2UvcG5nGCoiBmRpZ2VzdA=="));

        Assert.Equal(ProviderMessage.BodyOneofCase.Connect, connect.BodyCase);
        Assert.Equal((uint)1, connect.Connect.ProtocolVersion);
        Assert.Equal("key", connect.Connect.Authorization);
        Assert.Equal(ProviderMessage.BodyOneofCase.ResultUpload, resultUpload.BodyCase);
        Assert.Equal("handle", resultUpload.ResultUpload.TaskHandle);
        Assert.Equal("task", resultUpload.ResultUpload.TaskId);
        Assert.Equal("attempt", resultUpload.ResultUpload.AttemptId);
        Assert.Equal(ServerMessage.BodyOneofCase.Assignment, assignment.BodyCase);
        Assert.Equal("42", assignment.Assignment.Scalars["seed"]);
        Assert.Equal("https://input.example/a", assignment.Assignment.Input.Url);
        Assert.Equal("image/png", assignment.Assignment.Input.ContentType);
        Assert.Equal((ulong)42, assignment.Assignment.Input.Length);
        Assert.Equal("digest", assignment.Assignment.Input.Sha256);
        Assert.Equal("CgcIARoDa2V5", Convert.ToBase64String(connect.ToByteArray()));
        Assert.Equal("ElMKBHRhc2sSB2F0dGVtcHQaBmhhbmRsZSIKCgRzZWVkEgI0MiouChdodHRwczovL2lucHV0LmV4YW1wbGUvYRIJaW1hZ2UvcG5nGCoiBmRpZ2VzdA==", Convert.ToBase64String(assignment.ToByteArray()));
    }

    [Fact]
    public void Provider_assignment_and_data_plane_envelopes_round_trip_without_losing_ownership_fields()
    {
        var assignment = new ServerMessage
        {
            Assignment = new TaskAssignment { TaskId = "task", AttemptId = "attempt", TaskHandle = "handle", Scalars = { ["seed"] = "42" } },
        };
        var input = new ProviderMessage
        {
            InputDownload = new InputDownloadRequest { TaskId = "task", AttemptId = "attempt", TaskHandle = "handle" },
        };
        var upload = new ProviderMessage
        {
            ResultUpload = new ResultUploadRequest { TaskId = "task", AttemptId = "attempt", TaskHandle = "handle" },
        };
        var progress = new ProviderMessage
        {
            Progress = new ProgressUpdate { TaskId = "task", AttemptId = "attempt", TaskHandle = "handle", SequenceNumber = 9, Phase = "render", Percent = 50.5, Message = "half way" },
        };

        Assert.Equal(assignment, ServerMessage.Parser.ParseFrom(assignment.ToByteArray()));
        Assert.Equal(input, ProviderMessage.Parser.ParseFrom(input.ToByteArray()));
        Assert.Equal(upload, ProviderMessage.Parser.ParseFrom(upload.ToByteArray()));
        Assert.Equal(progress, ProviderMessage.Parser.ParseFrom(progress.ToByteArray()));
    }
}
