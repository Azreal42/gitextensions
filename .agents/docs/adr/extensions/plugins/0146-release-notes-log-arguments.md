# ADR 0146 — Personnaliser la lecture Git des notes de version

Statut : accepté. Validation Git/headless et limites dans le TODO, lot Compléments portables.

Intention : retrouver le champ Windows git log arguments sans perdre les bornes, le dernier résultat et ses exports.

Entrées : Windows ReleaseNotesGeneratorForm ; Linux PluginDialog / ReleaseNotesGeneratorPlugin, même parseur Windows GitLogLineParser.

Défaut : `--pretty="format:%h@%s%b" --abbrev-commit {0}..{1}` ; les bornes résolues alimentent le format .NET.

Décision : tokenizer partagé et argv littéraux, sans shell ; erreurs de format/quoting/Git explicites, ancien résultat conservé.

Le résultat est readonly ; texte/TSV/HTML et tableau structuré restent les exports existants.

Contrat portable : --output/--output= refusés ; --no-show-signature/--no-decorate garantissent le flux attendu par le parseur.

Repères/tests source : [plugin Linux](../../../../../src/linux/GitExtensions.Plugins/ReleaseNotesGeneratorPlugin.cs) ; [UI](../../../../../tests/linux/GitExtensions.Desktop.Tests/ReleaseNotesArgumentsDialogTests.cs) ; [dépôts réels](../../../../../tests/linux/GitExtensions.Plugins.Tests/ReleaseNotesArgumentsScenarios.cs).

Validation Git/headless liée au TODO ; formes d’arguments étendues et interactions installées ne sont pas déduites des sources de tests.
