# ADR-003: Strategia wersjonowania API

**Status:** Accepted  
**Data:** 2024-04-01  
**Decydenci:** @api-team, @arch-team  
**Związane ADR:** ADR-002 (niejasność: czy auth endpoints podlegają wersjonowaniu?)

## Kontekst

System rozrasta się: 15 mikroserwisów, 3 zewnętrznych partnerów API, mobile apps.
Potrzebujemy jasnej strategii wersjonowania pozwalającej na:
- Równoległe działanie v1 i v2 przez 6 miesięcy
- Jasna komunikacja deprecation do partnerów
- Brak konieczności koordynacji deploymentów między serwisami

Rozpatrywane strategie:
1. URL-based: `/api/v1/orders`, `/api/v2/orders`
2. Header-based: `Accept: application/vnd.company.v2+json`
3. Query parameter: `/api/orders?version=2`

## Decyzja

**URL-based versioning**: `/api/v{major}/resource`

Przykłady:
- `/api/v1/orders` — stara wersja (deprecated po 2025-01-01)
- `/api/v2/orders` — nowa wersja z paginacją i nowymi polami

**Reguły:**
- Major version bump: breaking changes (zmiana schematu, usunięcie pola)
- Deprecated versions: wspierane 6 miesięcy, `Deprecation` header w response
- Nie używamy minor/patch w URL (zmiany wstecznie kompatybilne nie wymagają new version)

**Powód wyboru URL-based:**
- Prostota dla developerów (widoczne w URL, łatwe do logowania)
- Prosta konfiguracja load balancer/routing
- Łatwa cache'owalność (różne cache keys)

## Konsekwencje

**Pozytywne:**
- Przejrzystość: URL jednoznacznie wskazuje wersję
- Łatwe A/B testing i canary deployments
- Żadna dodatkowa logika w API gateway

**Negatywne:**
- "REST purism" — URL powinien identyfikować resource, nie wersję (kontra argument)
- Duplikacja kodu kontrolerów (v1Controller vs v2Controller)
- Klienci muszą aktualizować hard-coded URLs przy upgrade

**Niejasności:**
- AuthService `/auth/validate` endpoint używany wewnętrznie przez serwisy (ADR-002).
  Czy podlega temu ADR? Jeśli tak — breaking changes w protokole walidacji wymagają
  inkrementacji version i synchronizacji wszystkich serwisów.
  Jeśli nie — jak dokumentujemy wyjątki?
  **STATUS: Nierozwiązane → patrz ADR-002 uwaga o potencjalnym ADR-005**

- Internal service-to-service communication: czy też używa `/api/v1/`?
  Lub czy mamy osobny schemat dla internal APIs?
  **STATUS: Nieudokumentowane**
