namespace Quacktion.Data.Preparation;
public sealed class PreparationSettings
{
    public bool Enabled { get; set; }
    public bool MemoryEnabled { get; set; }
    public int PollSeconds { get; set; } = 120;
    public int HorizonDays { get; set; } = 30;
    public string CalendarListTool { get; set; } = "";
    public string[] CalendarRequiredScopes { get; set; } = [];
    public string GmailSearchTool { get; set; } = "";
    public string[] GmailRequiredScopes { get; set; } = [];
    public string EmbeddingModel { get; set; } = "gemini-embedding-001";
    public int EmbeddingDimensions { get; set; } = 768;
    public string TextIndex { get; set; } = "meeting_memory_text";
    public string VectorIndex { get; set; } = "meeting_memory_vector";
    public bool NativeRankFusion { get; set; } = true;
    public bool SpokenNotifications { get; set; } = true;
    public string NotificationTtsModel { get; set; } = "gemini-2.5-flash-preview-tts";
    public string NotificationVoice { get; set; } = "Kore";
}
