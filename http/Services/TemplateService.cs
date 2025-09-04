using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Company.Function.Services
{
    public class TemplateService
    {
        private readonly ILogger<TemplateService> _logger;
        private readonly Dictionary<string, string> _templateCache = new();
        private readonly string _templateBasePath;

        public TemplateService(ILogger<TemplateService> logger)
        {
            _logger = logger;
            
            // Get the base path for templates relative to the assembly location
            var assemblyLocation = Assembly.GetExecutingAssembly().Location;
            var assemblyDirectory = Path.GetDirectoryName(assemblyLocation) ?? "";
            _templateBasePath = Path.Combine(assemblyDirectory, "Templates");
            
            _logger.LogInformation($"[TEMPLATE] Template base path: {_templateBasePath}");
        }

        public async Task<string> RenderTemplateAsync(string templateName, object model)
        {
            try
            {
                // Load template from cache or file
                var template = await LoadTemplateAsync(templateName);
                
                // Convert model to dictionary
                var modelDict = ConvertToDict(model);
                
                // Replace placeholders with values from the model
                var result = template;
                foreach (var kvp in modelDict)
                {
                    var placeholder = $"{{{{{kvp.Key}}}}}";
                    var value = kvp.Value?.ToString() ?? string.Empty;
                    result = result.Replace(placeholder, value);
                }
                
                _logger.LogDebug($"[TEMPLATE] Rendered template '{templateName}' successfully");
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[TEMPLATE-ERROR] Failed to render template '{templateName}'");
                throw new InvalidOperationException($"Failed to render template: {templateName}", ex);
            }
        }

        private async Task<string> LoadTemplateAsync(string templateName)
        {
            // Check cache first
            if (_templateCache.TryGetValue(templateName, out var cachedTemplate))
            {
                _logger.LogDebug($"[TEMPLATE] Using cached template '{templateName}'");
                return cachedTemplate;
            }

            // Load from file
            var templatePath = Path.Combine(_templateBasePath, templateName);
            if (!File.Exists(templatePath))
            {
                // Fallback to embedded resource if file doesn't exist
                _logger.LogInformation($"[TEMPLATE] Template file not found at '{templatePath}', trying embedded resource");
                var embeddedTemplate = await LoadEmbeddedTemplateAsync(templateName);
                if (!string.IsNullOrEmpty(embeddedTemplate))
                {
                    _templateCache[templateName] = embeddedTemplate;
                    return embeddedTemplate;
                }
                
                throw new FileNotFoundException($"Template not found: {templateName}");
            }

            var template = await File.ReadAllTextAsync(templatePath);
            _templateCache[templateName] = template;
            _logger.LogInformation($"[TEMPLATE] Loaded template '{templateName}' from file");
            return template;
        }

        private async Task<string> LoadEmbeddedTemplateAsync(string templateName)
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var resourceName = $"Company.Function.Templates.{Path.GetFileNameWithoutExtension(templateName)}.html";
                
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    _logger.LogWarning($"[TEMPLATE] Embedded resource not found: {resourceName}");
                    return string.Empty;
                }

                using var reader = new StreamReader(stream);
                var template = await reader.ReadToEndAsync();
                _logger.LogInformation($"[TEMPLATE] Loaded embedded template: {resourceName}");
                return template;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[TEMPLATE-ERROR] Failed to load embedded template: {templateName}");
                return string.Empty;
            }
        }

        private Dictionary<string, object> ConvertToDict(object obj)
        {
            if (obj is Dictionary<string, object> dict)
                return dict;
                
            var result = new Dictionary<string, object>();
            var properties = obj.GetType().GetProperties();
            
            foreach (var prop in properties)
            {
                result[prop.Name] = prop.GetValue(obj);
            }
            
            return result;
        }

        /// <summary>
        /// Render a template with conditional sections
        /// </summary>
        public async Task<string> RenderAdvancedTemplateAsync(string templateName, object model)
        {
            var template = await LoadTemplateAsync(templateName);
            var modelDict = ConvertToDict(model);
            
            // Process loops first (e.g., {{#each Items}}...{{/each}})
            template = ProcessLoops(template, modelDict);
            
            // Process conditional sections (e.g., {{#if AdminRole}}...{{/if}})
            template = ProcessConditionals(template, modelDict);
            
            // Replace simple placeholders, including array length expressions
            foreach (var kvp in modelDict)
            {
                var placeholder = $"{{{{{kvp.Key}}}}}";
                var value = kvp.Value?.ToString() ?? string.Empty;
                template = template.Replace(placeholder, value);
                
                // Handle .length expressions
                if (kvp.Value is System.Collections.ICollection collection)
                {
                    var lengthPlaceholder = $"{{{{{kvp.Key}.length}}}}";
                    template = template.Replace(lengthPlaceholder, collection.Count.ToString());
                }
                else if (kvp.Value is Array array)
                {
                    var lengthPlaceholder = $"{{{{{kvp.Key}.length}}}}";
                    template = template.Replace(lengthPlaceholder, array.Length.ToString());
                }
            }
            
            return template;
        }

        private string ProcessConditionals(string template, Dictionary<string, object> model)
        {
            // Use a more robust approach to handle large, complex conditional blocks
            // Process iteratively until no more conditionals are found
            
            int maxIterations = 10; // Prevent infinite loops
            int iteration = 0;
            
            while (iteration < maxIterations && ContainsConditionals(template))
            {
                var originalTemplate = template;
                
                template = ProcessIfElseBlocks(template, model);
                template = ProcessIfBlocks(template, model);  
                template = ProcessUnlessBlocks(template, model);
                
                // If no changes were made, break to prevent infinite loop
                if (template == originalTemplate)
                    break;
                    
                iteration++;
            }
            
            if (iteration >= maxIterations)
            {
                _logger.LogWarning("[TEMPLATE-CONDITIONAL] Maximum iterations reached, some conditionals may not be processed");
            }
            
            return template;
        }
        
        private bool ContainsConditionals(string template)
        {
            return template.Contains("{{#if ") || template.Contains("{{#unless ");
        }
        
        private string ProcessIfElseBlocks(string template, Dictionary<string, object> model)
        {
            // Process {{#if condition}}...{{else}}...{{/if}} blocks
            // Use manual parsing to handle very large blocks that regex might fail on
            int startPos = 0;
            while ((startPos = template.IndexOf("{{#if ", startPos)) != -1)
            {
                // Find the condition
                var conditionStart = startPos + 6; // Length of "{{#if "
                var conditionEnd = template.IndexOf("}}", conditionStart);
                if (conditionEnd == -1) break;
                
                var condition = template.Substring(conditionStart, conditionEnd - conditionStart).Trim();
                var blockStart = conditionEnd + 2; // After "}}"
                
                // Find matching {{else}} and {{/if}}
                var elsePos = FindMatchingElse(template, blockStart);
                var endIfPos = FindMatchingEndIf(template, blockStart);
                
                if (elsePos != -1 && endIfPos != -1 && elsePos < endIfPos)
                {
                    // This is an if-else block
                    var ifContent = template.Substring(blockStart, elsePos - blockStart);
                    var elseStart = elsePos + 8; // Length of "{{else}}"
                    var elseContent = template.Substring(elseStart, endIfPos - elseStart);
                    
                    var isTruthy = IsTruthy(condition, model);
                    _logger.LogInformation($"[TEMPLATE-CONDITIONAL] Processing {{{{#if {condition}}}}} - Result: {isTruthy}");
                    if (condition.Contains("length"))
                    {
                        _logger.LogInformation($"[TEMPLATE-DEBUG] Length condition detected: {condition}");
                    }
                    
                    var replacement = isTruthy ? ifContent : elseContent;
                    var fullBlockEnd = endIfPos + 7; // Length of "{{/if}}"
                    
                    template = template.Substring(0, startPos) + replacement + template.Substring(fullBlockEnd);
                    startPos = startPos + replacement.Length;
                }
                else if (endIfPos != -1)
                {
                    // This is a simple if block (no else)
                    var content = template.Substring(blockStart, endIfPos - blockStart);
                    var isTruthy = IsTruthy(condition, model);
                    _logger.LogInformation($"[TEMPLATE-CONDITIONAL] Processing {{{{#if {condition}}}}} (no else) - Result: {isTruthy}");
                    
                    var replacement = isTruthy ? content : string.Empty;
                    var fullBlockEnd = endIfPos + 7; // Length of "{{/if}}"
                    
                    template = template.Substring(0, startPos) + replacement + template.Substring(fullBlockEnd);
                    startPos = startPos + replacement.Length;
                }
                else
                {
                    startPos++;
                }
            }
            
            return template;
        }
        
        private string ProcessIfBlocks(string template, Dictionary<string, object> model)
        {
            var ifRegex = new Regex(@"\{\{#if\s+([^}]+)\}\}(.*?)\{\{/if\}\}", RegexOptions.Singleline);
            return ifRegex.Replace(template, match =>
            {
                var condition = match.Groups[1].Value.Trim();
                var content = match.Groups[2].Value;
                
                return IsTruthy(condition, model) ? content : string.Empty;
            });
        }
        
        private string ProcessUnlessBlocks(string template, Dictionary<string, object> model)
        {
            var unlessRegex = new Regex(@"\{\{#unless\s+([^}]+)\}\}(.*?)\{\{/unless\}\}", RegexOptions.Singleline);
            return unlessRegex.Replace(template, match =>
            {
                var condition = match.Groups[1].Value.Trim();
                var content = match.Groups[2].Value;
                
                return !IsTruthy(condition, model) ? content : string.Empty;
            });
        }
        
        private int FindMatchingElse(string template, int startPos)
        {
            int depth = 0;
            int pos = startPos;
            
            while (pos < template.Length)
            {
                if (template.Substring(pos, Math.Min(6, template.Length - pos)) == "{{#if ")
                {
                    depth++;
                    pos += 6;
                }
                else if (template.Substring(pos, Math.Min(7, template.Length - pos)) == "{{/if}}")
                {
                    if (depth == 0) return -1; // Found end before else
                    depth--;
                    pos += 7;
                }
                else if (template.Substring(pos, Math.Min(8, template.Length - pos)) == "{{else}}" && depth == 0)
                {
                    return pos;
                }
                else
                {
                    pos++;
                }
            }
            
            return -1;
        }
        
        private int FindMatchingEndIf(string template, int startPos)
        {
            int depth = 0;
            int pos = startPos;
            
            while (pos < template.Length)
            {
                if (template.Substring(pos, Math.Min(6, template.Length - pos)) == "{{#if ")
                {
                    depth++;
                    pos += 6;
                }
                else if (template.Substring(pos, Math.Min(7, template.Length - pos)) == "{{/if}}")
                {
                    if (depth == 0) return pos;
                    depth--;
                    pos += 7;
                }
                else
                {
                    pos++;
                }
            }
            
            return -1;
        }

        private bool IsTruthy(string condition, Dictionary<string, object> model)
        {
            // Handle property.length checks
            if (condition.EndsWith(".length"))
            {
                var propertyName = condition.Substring(0, condition.Length - 7);
                if (model.TryGetValue(propertyName, out var arrayValue))
                {
                    if (arrayValue is System.Collections.ICollection collection)
                        return collection.Count > 0;
                    if (arrayValue is Array array)
                        return array.Length > 0;
                }
                return false;
            }

            // Handle simple property checks
            if (model.TryGetValue(condition, out var value))
            {
                if (value is bool boolValue)
                    return boolValue;
                if (value is string stringValue)
                    return !string.IsNullOrEmpty(stringValue);
                if (value is int intValue)
                    return intValue > 0;
                if (value is double doubleValue)
                    return doubleValue > 0;
                if (value is System.Collections.ICollection collectionValue)
                    return collectionValue.Count > 0;
                if (value is Array arrayValue)
                    return arrayValue.Length > 0;
                // Non-null object is truthy
                return value != null;
            }
            
            return false;
        }

        private string ProcessLoops(string template, Dictionary<string, object> model)
        {
            var loopRegex = new Regex(@"\{\{#each\s+(\w+)\}\}(.*?)\{\{/each\}\}", RegexOptions.Singleline);
            
            return loopRegex.Replace(template, match =>
            {
                var collectionName = match.Groups[1].Value;
                var itemTemplate = match.Groups[2].Value;
                
                if (!model.TryGetValue(collectionName, out var value))
                    return string.Empty;
                
                // Handle different collection types
                System.Collections.IEnumerable collection = null;
                if (value is System.Collections.IEnumerable enumerable)
                    collection = enumerable;
                else
                    return string.Empty;
                
                var result = new List<string>();
                foreach (var item in collection)
                {
                    var itemResult = itemTemplate;
                    
                    // Handle {{this.Property}} references
                    var thisPropertyRegex = new Regex(@"\{\{this\.(\w+)\}\}");
                    itemResult = thisPropertyRegex.Replace(itemResult, thisMatch =>
                    {
                        var propertyName = thisMatch.Groups[1].Value;
                        
                        // Try to get property value from item
                        if (item != null)
                        {
                            var itemType = item.GetType();
                            var property = itemType.GetProperty(propertyName);
                            if (property != null)
                            {
                                var propertyValue = property.GetValue(item);
                                return propertyValue?.ToString() ?? string.Empty;
                            }
                            
                            // If it's a dictionary, try key lookup
                            if (item is Dictionary<string, object> dict && dict.TryGetValue(propertyName, out var dictValue))
                            {
                                return dictValue?.ToString() ?? string.Empty;
                            }
                        }
                        
                        return string.Empty;
                    });
                    
                    // Handle nested {{#unless this.Property}} blocks within each loop
                    var unlessThisRegex = new Regex(@"\{\{#unless this\.(\w+)\}\}(.*?)\{\{/unless\}\}", RegexOptions.Singleline);
                    itemResult = unlessThisRegex.Replace(itemResult, unlessMatch =>
                    {
                        var propertyName = unlessMatch.Groups[1].Value;
                        var unlessContent = unlessMatch.Groups[2].Value;
                        
                        bool hasValue = false;
                        if (item != null)
                        {
                            var itemType = item.GetType();
                            var property = itemType.GetProperty(propertyName);
                            if (property != null)
                            {
                                var propertyValue = property.GetValue(item);
                                if (propertyValue is string stringValue)
                                    hasValue = !string.IsNullOrEmpty(stringValue);
                                else
                                    hasValue = propertyValue != null;
                            }
                        }
                        
                        return hasValue ? string.Empty : unlessContent;
                    });
                    
                    result.Add(itemResult);
                }
                
                return string.Join("", result);
            });
        }
    }
}