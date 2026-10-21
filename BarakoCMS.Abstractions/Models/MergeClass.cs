namespace barakoCMS.Models;

/// <summary>How edits made on two stations are merged when they sync.</summary>
public enum MergeClass { Append = 0, FieldLww = 1, OwnedRequest = 2 }
