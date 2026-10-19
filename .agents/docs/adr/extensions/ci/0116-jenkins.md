# ADR 0116 — Jenkins

Reprendre URL, projets et filtre de branches Windows dans la page CI commune. Projets multiples, dossiers/multibranch et variables de dépôt se résolvent avant la lecture ; les identifiants restent distincts des XML.

Guest ou Basic sont choisis explicitement dans le dialogue partagé et enregistrés par fournisseur et endpoint complet, chemin inclus. Aucun Bearer ou compte système implicite. `JenkinsApiClient` possède les GET bornés, cache/ETag, annulation et drainage ; `JenkinsSnapshot` garde les priorités et dates du watcher Windows.

L'historique admis et les émissions nouvelles restent distincts : un plafond ne publie pas le statut d'un build écarté, une ancienne fin ne masque pas une nouvelle exécution. `JenkinsBuildSource` rejoint les lignes et le polling partagés. Rapport HTML embarqué et serveur réel restent ouverts.
