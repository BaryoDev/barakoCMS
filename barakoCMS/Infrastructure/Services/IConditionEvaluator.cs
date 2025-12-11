namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Service for evaluating permission conditions
/// </summary>
public interface IConditionEvaluator
{
    /// <summary>
    /// Evaluate if conditions match the content and user context
    /// </summary>
    /// <param name="conditions">Condition dictionary (Directus/Strapi style)</param>
    /// <param name="contentData">Content data to evaluate against</param>
    /// <param name="user">Current user for $CURRENT_USER placeholder</param>
    /// <returns>True if conditions match, false otherwise</returns>
    bool Evaluate(
        Dictionary<string, object> conditions,
        Dictionary<string, object> contentData,
        Models.User user);
}
