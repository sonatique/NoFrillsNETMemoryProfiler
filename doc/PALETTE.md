# No Frills .NET Memory Profiler — Palette

Palette derivee de l'icone taupe. Base sombre et terreuse (coherente avec un
outil dev "sans prétention"), un seul accent vif reserve aux actions et aux
elements de donnees actifs (barres memoire, selection, etat en cours).

## Neutres (fond / structure)

| Role                  | Hex       | Usage                                          |
|------------------------|-----------|-------------------------------------------------|
| Background             | `#201B18` | Fond principal de la fenetre                    |
| Surface                | `#2E2622` | Panneaux, cartes, sidebar                       |
| Surface elevee         | `#3A322D` | Popups, tooltips, elements au-dessus de Surface |
| Bordure / separateur   | `#4A3F3A` | Lignes de separation, contours de champs        |
| Texte principal        | `#ECE6E1` | Texte courant sur fond sombre                   |
| Texte secondaire       | `#A89A92` | Labels, texte desactive, annotations            |

## Marque (brand)

| Role                  | Hex       | Usage                                          |
|------------------------|-----------|-------------------------------------------------|
| Brun taupe (identite)  | `#6B5C56` | Logo, header, elements de marque non interactifs|

## Accent (unique, reserve a l'action et aux donnees actives)

| Role                  | Hex       | Usage                                          |
|------------------------|-----------|-------------------------------------------------|
| Accent                 | `#4FAE7A` | Boutons primaires, bordure active, barre memoire en cours |
| Accent survol          | `#63C48F` | Etat hover                                      |
| Accent presse          | `#3D8F63` | Etat pressed / actif                            |

## Badge technique (icone uniquement)

| Role                  | Hex       | Usage                                          |
|------------------------|-----------|-------------------------------------------------|
| .NET (officiel)        | `#512BD4` | Chip de coin sur l'icone detaillee uniquement, pour signaler la techno cible. Ne pas utiliser ailleurs dans l'UI. |

## Semantiques (etats)

| Role     | Hex       | Usage                          |
|-----------|-----------|----------------------------------|
| Succes    | `#4FAE7A` | Reutilise l'accent (pas de doublon de vert) |
| Warning   | `#D9A441` | Seuils memoire eleves, avertissements |
| Erreur    | `#C1554A` | Fuites detectees, echecs        |
| Info      | `#5B8FAE` | Messages neutres, aide contextuelle |

## Regle d'usage

- Un seul accent vif (`Accent`) dans toute l'interface : il marque ce qui
  est actionnable ou ce qui represente une donnee "vivante" (allocation en
  cours, selection active). Tout le reste reste dans les neutres.
- Warning / Erreur / Info restent reserves aux etats systeme, jamais utilises
  pour de la decoration.
- Contraste texte principal sur fond : `#ECE6E1` sur `#201B18` (ratio eleve,
  lisible en continu pour une appli technique consultee longtemps).
