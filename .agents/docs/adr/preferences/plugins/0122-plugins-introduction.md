# ADR 0122 — Introduction aux paramètres Plugins

Donner au groupe Plugins de Settings la même page d'accueil que Windows : inviter à choisir un sous-nœud pour consulter les paramètres d'un plugin.

`PluginRootIntroductionPage` reprend le seul label original et ses traductions. `DesktopSettingsDialog` l'insère avec l'icône existante, la recherche et la navigation communes. Passer par cette page ne modifie ni brouillon ni réglage.

Catalogue, installation et activation ne font pas partie de cette page Windows. Les pages propres aux plugins et leur cycle de vie sont évalués séparément ; ce label ne promet pas leur portage. Repères : `SettingsGroupPages`, `DesktopSettingsDialog`, `PluginRootIntroductionPageTests`.
