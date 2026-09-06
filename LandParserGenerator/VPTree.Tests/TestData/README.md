# TestData

`anchors_object.json` — реальный корпус: 1956 полей GraphQL-схем из 12 OSS-проектов
(`test_repos_graphql2/`), извлечён скриптом `markup/experiments/vp_tree_metric_subsets/scripts/01_parse_graphql.py`.
Это побайтовая копия `markup/experiments/_data/anchors_object.json`.

Используется тестами `MetricAxiomTests` (аксиомы метрики на реальных тройках)
и `ExactnessTests` (VP-дерево ≡ полный перебор, детерминизм).
