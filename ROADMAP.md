# Folio — feuille de route

Éditeur PDF natif macOS (clone de PDF Expert), construit par étapes.

## ✅ Étape 1 — Lecteur
- Ouvrir / enregistrer / nouveau document (DocumentGroup), onglets et fichiers récents natifs
- Vignettes des pages dans la barre latérale
- Zoom (+, -, taille réelle, ajuster), modes page unique / continu / deux pages
- Navigation entre pages, indicateur « page x / n »
- Recherche plein texte avec surlignage et résultat suivant/précédent (⌘G / ⇧⌘G)

## ✅ Étape 2 — Annotations
- Barre d'outils : sélection, surligner, souligner, barrer, note, zone de texte, dessin, rectangle, ellipse, ligne, flèche
- Couleur par outil, épaisseur de trait
- Sélection (contour pointillé), déplacement, suppression (⌫), Échap → outil sélection
- Inspecteur : couleur, épaisseur, texte / commentaire
- Mise en forme des zones de texte : police, taille, gras, italique, souligné, barré, alignement, fond
  (`FreeTextAnnotation` dessine elle-même le texte ; rendu intégré au PDF enregistré, styles gardés dans `/FolioStyle`)
- Annuler / rétablir (UndoManager → marque le document comme modifié)
- Reste à faire plus tard : poignées de redimensionnement, remplissage des formes, édition du texte directement sur la page, liste des annotations

## ⬜ Étape 3 — Gestion des pages
- Pivoter, supprimer, dupliquer, réordonner (glisser-déposer dans les vignettes)
- Insérer une page vierge ou depuis un autre PDF / une image
- Fusionner des PDF, extraire des pages

## ⬜ Étape 4 — Formulaires & signature
- Remplissage des champs de formulaire
- Signature dessinée/enregistrée, tampon, image

## ⬜ Étape 5 — Édition de texte & extras
- Aplatir les annotations (toutes ou la sélection) : les intégrer au contenu de la page pour que leur texte
  devienne sélectionnable/surlignable et qu'elles ne soient plus modifiables ; option « Exporter aplati »
- Ajouter du texte, masquer/caviarder (redaction)
- Édition du texte existant (difficile avec PDFKit : surcouche « effacer + réécrire »)
- Signets / table des matières, export image, compression, mot de passe
- Icône de l'app, préférences
