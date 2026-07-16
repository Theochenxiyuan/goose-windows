use anyhow::Result;
use goose::config::{paths::Paths, Config};
use goose::providers::inventory::{ProviderInventoryEntry, ProviderInventoryService};
use goose::session::SessionManager;
use goose_providers::model::ModelConfig;
use serde::Serialize;

const SCHEMA_VERSION: u32 = 1;

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct LauncherOptionsCatalog {
    schema_version: u32,
    default_provider: Option<String>,
    default_model: Option<String>,
    default_thinking_effort: Option<String>,
    providers: Vec<LauncherProvider>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct LauncherProvider {
    id: String,
    name: String,
    models: Vec<LauncherModel>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct LauncherModel {
    id: String,
    name: String,
    reasoning: bool,
    thinking_efforts: Vec<&'static str>,
}

fn launcher_provider(
    mut entry: ProviderInventoryEntry,
    default_provider: Option<&str>,
    default_model: Option<&str>,
) -> Option<LauncherProvider> {
    if !entry.configured && default_provider != Some(entry.provider_id.as_str()) {
        return None;
    }

    if default_provider == Some(entry.provider_id.as_str()) {
        if let Some(default_model) = default_model {
            if !entry.models.iter().any(|model| model.id == default_model) {
                entry.models.insert(
                    0,
                    goose::providers::inventory::InventoryModel {
                        id: default_model.to_string(),
                        name: default_model.to_string(),
                        family: None,
                        context_limit: None,
                        reasoning: None,
                        recommended: false,
                    },
                );
            }
        }
    }

    let mut models = entry
        .models
        .into_iter()
        .map(|model| {
            let reasoning = model
                .reasoning
                .unwrap_or_else(|| ModelConfig::new(&model.id).is_reasoning_model());
            LauncherModel {
                reasoning,
                thinking_efforts: if reasoning {
                    vec!["off", "low", "medium", "high", "max"]
                } else {
                    vec![]
                },
                id: model.id,
                name: model.name,
            }
        })
        .collect::<Vec<_>>();
    models.sort_by(|left, right| left.name.cmp(&right.name).then(left.id.cmp(&right.id)));
    models.dedup_by(|left, right| left.id == right.id);

    Some(LauncherProvider {
        id: entry.provider_id,
        name: entry.provider_name,
        models,
    })
}

pub async fn handle_launcher_options() -> Result<()> {
    let config = Config::global();
    let default_provider = config.get_goose_provider().ok();
    let default_model = config.get_goose_model().ok();
    let default_thinking_effort = config
        .get_goose_thinking_effort()
        .map(|effort| effort.to_string());
    let session_manager = SessionManager::new(Paths::data_dir());
    let inventory = ProviderInventoryService::new(session_manager.storage().clone());
    let entries = inventory.entries(&[]).await?;
    let mut providers = entries
        .into_iter()
        .filter_map(|entry| {
            launcher_provider(entry, default_provider.as_deref(), default_model.as_deref())
        })
        .collect::<Vec<_>>();
    providers.sort_by(|left, right| left.name.cmp(&right.name).then(left.id.cmp(&right.id)));

    serde_json::to_writer(
        std::io::stdout(),
        &LauncherOptionsCatalog {
            schema_version: SCHEMA_VERSION,
            default_provider,
            default_model,
            default_thinking_effort,
            providers,
        },
    )?;
    Ok(())
}
