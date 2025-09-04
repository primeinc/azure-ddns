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
            
            // Process conditional sections first (e.g., {{#if AdminRole}}...{{/if}})
            template = ProcessConditionals(template, modelDict);
            
            // Process loops (e.g., {{#each Items}}...{{/each}})
            template = ProcessLoops(template, modelDict);
            
            // Replace simple placeholders
            foreach (var kvp in modelDict)
            {
                var placeholder = $"{{{{{kvp.Key}}}}}";
                var value = kvp.Value?.ToString() ?? string.Empty;
                template = template.Replace(placeholder, value);
            }
            
            return template;
        }

        private string ProcessConditionals(string template, Dictionary<string, object> model)
        {
            var conditionalRegex = new Regex(@"\{\{#if\s+(\w+)\}\}(.*?)\{\{/if\}\}", RegexOptions.Singleline);
            
            return conditionalRegex.Replace(template, match =>
            {
                var condition = match.Groups[1].Value;
                var content = match.Groups[2].Value;
                
                if (model.TryGetValue(condition, out var value))
                {
                    // Check if the value is truthy
                    if (value is bool boolValue && boolValue)
                        return content;
                    if (value is string stringValue && !string.IsNullOrEmpty(stringValue))
                        return content;
                    if (value is int intValue && intValue > 0)
                        return content;
                }
                
                return string.Empty;
            });
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
                
                if (value is not IEnumerable<Dictionary<string, object>> collection)
                    return string.Empty;
                
                var result = new List<string>();
                foreach (var item in collection)
                {
                    var itemResult = itemTemplate;
                    foreach (var kvp in item)
                    {
                        var placeholder = $"{{{{{kvp.Key}}}}}";
                        itemResult = itemResult.Replace(placeholder, kvp.Value?.ToString() ?? string.Empty);
                    }
                    result.Add(itemResult);
                }
                
                return string.Join("", result);
            });
        }
    }
}