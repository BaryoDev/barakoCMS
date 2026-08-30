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

    /// <summary>
    /// Evaluate conditions against a whole content document, so a rule can name a document property
    /// as well as a schema field.
    /// </summary>
    /// <remarks>
    /// Ownership is the reason this exists. <c>{"$createdBy": {"_eq": "$CURRENT_USER"}}</c> is the
    /// rule for "own records only", and <c>CreatedBy</c> lives on the document rather than in the
    /// data bag, so the older overload cannot see it.
    ///
    /// Document properties are named with a <c>$</c> prefix. A schema field cannot collide, because
    /// <c>ContentTypeValidatorService</c> requires a field name to start with an uppercase letter,
    /// so the two namespaces are separated by a rule that is already enforced rather than by
    /// convention.
    ///
    /// A default implementation keeps existing implementors compiling, per CLAUDE.md section 6. It
    /// ignores document properties, which is the behaviour they have today.
    /// </remarks>
    bool Evaluate(
        Dictionary<string, object> conditions,
        Models.Content content,
        Models.User user)
        => Evaluate(conditions, content.Data, user);
}
