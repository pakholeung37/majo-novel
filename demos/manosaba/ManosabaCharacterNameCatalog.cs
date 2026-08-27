#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;

namespace Majo.Demos.Manosaba;

public sealed class ManosabaCharacterNameCatalog
{
	private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> names;
	private readonly string fallbackLocale;

	private ManosabaCharacterNameCatalog(
		IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> names,
		string fallbackLocale)
	{
		this.names = names;
		this.fallbackLocale = fallbackLocale;
	}

	public static ManosabaCharacterNameCatalog Load(string resourcePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(resourcePath);
		var json = FileAccess.GetFileAsString(resourcePath);
		if (string.IsNullOrWhiteSpace(json))
		{
			throw new InvalidOperationException(
				$"Character name catalog '{resourcePath}' is empty or unavailable.");
		}

		return Parse(json, resourcePath);
	}

	public static ManosabaCharacterNameCatalog Parse(
		string json,
		string sourceName = "<memory>")
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(json);
		var document = JsonSerializer.Deserialize<CatalogDocument>(
			json,
			new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
			throw new InvalidOperationException(
				$"Character name catalog '{sourceName}' is invalid.");
		var names = document.Names.ToDictionary(
			entry => entry.Key,
			entry => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(
				entry.Value,
				StringComparer.OrdinalIgnoreCase),
			StringComparer.OrdinalIgnoreCase);
		return new ManosabaCharacterNameCatalog(names, document.FallbackLocale);
	}

	public string Resolve(string speakerId, string locale)
	{
		if (string.IsNullOrWhiteSpace(speakerId) || !names.TryGetValue(speakerId, out var localized))
		{
			return speakerId;
		}

		foreach (var candidate in GetLocaleCandidates(locale))
		{
			if (localized.TryGetValue(candidate, out var name) &&
				!string.IsNullOrWhiteSpace(name))
			{
				return name;
			}
		}

		return speakerId;
	}

	private IEnumerable<string> GetLocaleCandidates(string locale)
	{
		var normalized = string.IsNullOrWhiteSpace(locale)
			? fallbackLocale
			: locale.Trim().Replace('_', '-');
		yield return normalized;

		var language = normalized.Split('-', 2)[0];
		if (language.Equals("zh", StringComparison.OrdinalIgnoreCase))
		{
			var traditional = normalized.Contains("Hant", StringComparison.OrdinalIgnoreCase) ||
				normalized.EndsWith("-TW", StringComparison.OrdinalIgnoreCase) ||
				normalized.EndsWith("-HK", StringComparison.OrdinalIgnoreCase) ||
				normalized.EndsWith("-MO", StringComparison.OrdinalIgnoreCase);
			yield return traditional ? "zh-Hant" : "zh-Hans";
		}
		else if (!language.Equals(normalized, StringComparison.OrdinalIgnoreCase))
		{
			yield return language;
		}

		if (!fallbackLocale.Equals(normalized, StringComparison.OrdinalIgnoreCase))
		{
			yield return fallbackLocale;
		}
	}

	private sealed class CatalogDocument
	{
		public string FallbackLocale { get; init; } = "ja";

		public Dictionary<string, Dictionary<string, string>> Names { get; init; } =
			new(StringComparer.OrdinalIgnoreCase);
	}
}
