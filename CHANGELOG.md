# Changelog

## [0.2.0](https://github.com/chasepie/lookout/compare/v0.1.0...v0.2.0) (2026-11-16)


### ⚠ BREAKING CHANGES

* deploy/docker-compose.yaml changes, adding the optional LOOKOUT_MAX_CONCURRENT_RUNS setting. Existing deploy/.env files keep working, since Compose falls back to the default.

### Features

* let watch scripts ask to fail the VPN over to another region ([#25](https://github.com/chasepie/lookout/issues/25)) ([ee496e7](https://github.com/chasepie/lookout/commit/ee496e76c971f8c9da7e41e033f0c7bc513104fd))
* run watches in parallel, up to LOOKOUT_MAX_CONCURRENT_RUNS ([#27](https://github.com/chasepie/lookout/issues/27)) ([2ffc034](https://github.com/chasepie/lookout/commit/2ffc034104e4a353487572225267530c52375d4d))


### Bug Fixes

* make the VPN one module, and stop its region check undoing switches ([#33](https://github.com/chasepie/lookout/issues/33)) ([41e5803](https://github.com/chasepie/lookout/commit/41e580340e4f91642bd384c33bc7f7373f30f83a))

## 0.1.0 (2026-11-11)


### Features

* release with release-please, and publish the watch SDK ([#23](https://github.com/chasepie/lookout/issues/23)) ([a843bec](https://github.com/chasepie/lookout/commit/a843becd83489074b2002afb63bc05171675959f))
