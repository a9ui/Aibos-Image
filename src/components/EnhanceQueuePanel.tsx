'use client';

import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  DEFAULT_ENHANCEMENT_SETTINGS,
  ENHANCEMENT_PRESETS,
  type EnhancementDiagnostics,
  type EnhancementPreset,
  type EnhancementRequestSettings,
} from '@/lib/enhance/types';
import { useImageStore } from '../store/ImageContext';

type EnhancementJobStatus = 'queued' | 'running' | 'succeeded' | 'failed' | 'canceled' | 'deleted';

type EnhancementSettings = EnhancementRequestSettings;

interface EnhancementJob {
  id: string;
  sourceId: string;
  sourcePath: string;
  presetId: string;
  preset?: EnhancementPreset;
  adapterId: string;
  status: EnhancementJobStatus;
  progress: number;
  outputPath?: string;
  errorMessage?: string;
  createdAt: string;
  updatedAt: string;
  diagnostics?: EnhancementDiagnostics;
}

export function getEnhancementSettings(): EnhancementSettings {
  try {
    const raw = localStorage.getItem('pvu_enhance_settings');
    if (!raw) return DEFAULT_ENHANCEMENT_SETTINGS;
    const parsed = JSON.parse(raw) as Partial<EnhancementSettings>;
    const adapterId = parsed.adapterId === 'comfyui' || parsed.adapterId === 'sharp-test' || parsed.adapterId === 'realesrgan-ncnn'
      ? parsed.adapterId
      : DEFAULT_ENHANCEMENT_SETTINGS.adapterId;
    const maxScale = adapterId === 'realesrgan-ncnn' ? 4 : 8;
    return {
      presetId: parsed.presetId || DEFAULT_ENHANCEMENT_SETTINGS.presetId,
      adapterId,
      scale: Math.max(1, Math.min(maxScale, Number(parsed.scale) || DEFAULT_ENHANCEMENT_SETTINGS.scale)),
      denoise: Math.max(0, Math.min(100, Number(parsed.denoise ?? DEFAULT_ENHANCEMENT_SETTINGS.denoise))),
      sharpen: Math.max(0, Math.min(100, Number(parsed.sharpen ?? DEFAULT_ENHANCEMENT_SETTINGS.sharpen))),
      detail: Math.max(0, Math.min(100, Number(parsed.detail ?? DEFAULT_ENHANCEMENT_SETTINGS.detail))),
      smoothness: Math.max(0, Math.min(100, Number(parsed.smoothness ?? DEFAULT_ENHANCEMENT_SETTINGS.smoothness))),
      colorBrightness: Math.max(-100, Math.min(100, Number(parsed.colorBrightness ?? DEFAULT_ENHANCEMENT_SETTINGS.colorBrightness))),
      colorContrast: Math.max(-100, Math.min(100, Number(parsed.colorContrast ?? DEFAULT_ENHANCEMENT_SETTINGS.colorContrast))),
      colorSaturation: Math.max(-100, Math.min(100, Number(parsed.colorSaturation ?? DEFAULT_ENHANCEMENT_SETTINGS.colorSaturation))),
      outputFormat:
        parsed.outputFormat === 'webp' || parsed.outputFormat === 'jpg' || parsed.outputFormat === 'png'
          ? parsed.outputFormat
          : DEFAULT_ENHANCEMENT_SETTINGS.outputFormat,
    };
  } catch {
    return DEFAULT_ENHANCEMENT_SETTINGS;
  }
}

export function saveEnhancementSettings(settings: EnhancementSettings) {
  try {
    localStorage.setItem('pvu_enhance_settings', JSON.stringify(settings));
  } catch {
    // Ignore persistence failures; the request still carries the live settings.
  }
}

function describePresetSettings(preset: EnhancementPreset | undefined) {
  if (!preset) return '';
  return `${preset.modelFamily} / ${preset.modelName} / ${preset.scale}x / ${preset.outputFormat.toUpperCase()} / denoise ${preset.denoise} / sharpen ${preset.sharpen} / detail ${preset.detail} / smooth ${preset.smoothness} / brightness ${preset.colorBrightness ?? 0} / contrast ${preset.colorContrast ?? 0} / saturation ${preset.colorSaturation ?? 0}`;
}

function describeJobSettings(job: EnhancementJob) {
  const preset = job.preset;
  if (!preset) return job.presetId;
  return `${preset.label} / ${preset.modelFamily} / ${preset.scale}x / ${preset.outputFormat.toUpperCase()} / denoise ${preset.denoise} / sharpen ${preset.sharpen} / detail ${preset.detail ?? 0} / smooth ${preset.smoothness ?? 0} / brightness ${preset.colorBrightness ?? 0} / contrast ${preset.colorContrast ?? 0} / saturation ${preset.colorSaturation ?? 0}`;
}

function formatMs(ms: number | undefined) {
  if (typeof ms !== 'number') return '';
  if (ms < 1000) return `${ms}ms`;
  return `${Math.round(ms / 100) / 10}s`;
}

function describeDiagnostics(job: EnhancementJob) {
  const diagnostics = job.diagnostics;
  if (!diagnostics) return '';
  const parts = [
    diagnostics.modelName ? diagnostics.modelName : '',
    diagnostics.sourceMP ? `source ${diagnostics.sourceMP}MP` : '',
    diagnostics.workMP ? `AI work ${diagnostics.workMP}MP` : '',
    diagnostics.targetMP ? `target ${diagnostics.targetMP}MP` : '',
    diagnostics.nativeScale && diagnostics.requestedScale && diagnostics.nativeScale !== diagnostics.requestedScale
      ? `native ${diagnostics.nativeScale}x -> ${diagnostics.requestedScale}x`
      : '',
    diagnostics.processorMs ? `engine ${formatMs(diagnostics.processorMs)}` : '',
    diagnostics.ncnnMs ? `ncnn ${formatMs(diagnostics.ncnnMs)}` : '',
    diagnostics.comfyWaitMs ? `Comfy ${formatMs(diagnostics.comfyWaitMs)}` : '',
    diagnostics.postprocessMs ? `post ${formatMs(diagnostics.postprocessMs)}` : '',
    diagnostics.totalMs ? `total ${formatMs(diagnostics.totalMs)}` : '',
  ].filter(Boolean);
  return parts.join(' / ');
}

export async function createEnhancementJob(
  sourceId: string,
  settings = getEnhancementSettings(),
  indexToken?: string | null
) {
  const requestJob = async (confirmLargeJob = false) => fetch('/api/enhance/jobs', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      sourceId,
      ...settings,
      ...(indexToken ? { indexToken } : {}),
      ...(confirmLargeJob ? { confirmLargeJob: true } : {}),
    }),
  });
  let res = await requestJob();
  let data = await res.json().catch(() => ({}));
  if (res.status === 409 && data.code === 'UPSCALE_REQUIRES_CONFIRMATION') {
    const diagnostics = data.diagnostics as EnhancementDiagnostics | undefined;
    const ok = window.confirm(
      `This upscale may be slow.\n\nAI work: ${diagnostics?.workMP ?? '?'}MP\nOutput: ${diagnostics?.targetMP ?? '?'}MP\n\nStart anyway?`
    );
    if (ok) {
      res = await requestJob(true);
      data = await res.json().catch(() => ({}));
    }
  }
  if (!res.ok) throw new Error(data.error || 'Failed to create enhancement job');
  window.dispatchEvent(new Event('pvu-enhance-jobs-changed'));
  return data.job as EnhancementJob;
}

export async function deleteEnhancementOutput(jobId: string) {
  const res = await fetch(`/api/enhance/jobs/${encodeURIComponent(jobId)}/output`, { method: 'DELETE' });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.error || 'Failed to delete enhanced output');
  window.dispatchEvent(new Event('pvu-enhance-jobs-changed'));
  return data.job as EnhancementJob;
}

export async function cancelEnhancementJob(jobId: string) {
  const res = await fetch(`/api/enhance/jobs/${encodeURIComponent(jobId)}/cancel`, { method: 'POST' });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.error || 'Failed to cancel enhancement job');
  window.dispatchEvent(new Event('pvu-enhance-jobs-changed'));
  return data.job as EnhancementJob;
}

export function EnhanceSettingsControls() {
  const [settings, setSettings] = useState<EnhancementSettings>(DEFAULT_ENHANCEMENT_SETTINGS);

  useEffect(() => {
    setSettings(getEnhancementSettings());
  }, []);

  const applyPreset = (presetId: string) => {
    const preset = ENHANCEMENT_PRESETS.find((candidate) => candidate.id === presetId) ?? ENHANCEMENT_PRESETS[0];
    const next = {
      presetId: preset.id,
      adapterId: settings.adapterId,
      scale: settings.adapterId === 'realesrgan-ncnn' ? Math.min(4, preset.scale) : preset.scale,
      denoise: preset.denoise,
      sharpen: preset.sharpen,
      detail: preset.detail,
      smoothness: preset.smoothness,
      colorBrightness: preset.colorBrightness,
      colorContrast: preset.colorContrast,
      colorSaturation: preset.colorSaturation,
      outputFormat: preset.outputFormat,
    };
    setSettings(next);
    saveEnhancementSettings(next);
  };

  const patchSettings = (patch: Partial<EnhancementSettings>) => {
    const next = { ...settings, ...patch };
    if (next.adapterId === 'realesrgan-ncnn' && next.scale > 4) next.scale = 4;
    setSettings(next);
    saveEnhancementSettings(next);
  };

  const selectedPreset = ENHANCEMENT_PRESETS.find((preset) => preset.id === settings.presetId) ?? ENHANCEMENT_PRESETS[0];
  const scaleOptions = settings.adapterId === 'realesrgan-ncnn' ? [1.5, 2, 3, 4] : [1.5, 2, 3, 4, 6, 8];

  return (
    <div className="enhance-settings">
      <label>
        <span>Method</span>
        <select value={settings.adapterId} onChange={(event) => patchSettings({ adapterId: event.target.value })}>
          <option value="sharp-test">Simple local resize</option>
          <option value="realesrgan-ncnn">Real-ESRGAN fast GPU</option>
          <option value="comfyui">ComfyUI AI upscale</option>
        </select>
      </label>
      <label>
        <span>Model</span>
        <select value={settings.presetId} onChange={(event) => applyPreset(event.target.value)}>
          {ENHANCEMENT_PRESETS.map((preset) => (
            <option key={preset.id} value={preset.id}>{preset.label}</option>
          ))}
        </select>
      </label>
      <div className="enhance-model-hint">{describePresetSettings(selectedPreset)}</div>
      <label>
        <span>Scale</span>
        <select value={settings.scale} onChange={(event) => patchSettings({ scale: Number(event.target.value) })}>
          {scaleOptions.map((scale) => (
            <option key={scale} value={scale}>{scale}x</option>
          ))}
        </select>
      </label>
      {settings.adapterId === 'realesrgan-ncnn' && (
        <div className="enhance-model-hint">
          Recommended. Uses local Real-ESRGAN ncnn-vulkan without ComfyUI. 2x/3x are fastest; 4x can take longer on large images.
        </div>
      )}
      {settings.adapterId === 'sharp-test' ? (
        <>
          <label>
            <span>Denoise {settings.denoise}</span>
            <input type="range" min="0" max="100" value={settings.denoise} onChange={(event) => patchSettings({ denoise: Number(event.target.value) })} />
          </label>
          <label>
            <span>Sharpen {settings.sharpen}</span>
            <input type="range" min="0" max="100" value={settings.sharpen} onChange={(event) => patchSettings({ sharpen: Number(event.target.value) })} />
          </label>
          <label>
            <span>Detail {settings.detail}</span>
            <input type="range" min="0" max="100" value={settings.detail} onChange={(event) => patchSettings({ detail: Number(event.target.value) })} />
          </label>
          <label>
            <span>Smoothness {settings.smoothness}</span>
            <input type="range" min="0" max="100" value={settings.smoothness} onChange={(event) => patchSettings({ smoothness: Number(event.target.value) })} />
          </label>
          <label>
            <span>Brightness {settings.colorBrightness}</span>
            <input type="range" min="-100" max="100" value={settings.colorBrightness} onChange={(event) => patchSettings({ colorBrightness: Number(event.target.value) })} />
          </label>
          <label>
            <span>Contrast {settings.colorContrast}</span>
            <input type="range" min="-100" max="100" value={settings.colorContrast} onChange={(event) => patchSettings({ colorContrast: Number(event.target.value) })} />
          </label>
          <label>
            <span>Saturation {settings.colorSaturation}</span>
            <input type="range" min="-100" max="100" value={settings.colorSaturation} onChange={(event) => patchSettings({ colorSaturation: Number(event.target.value) })} />
          </label>
        </>
      ) : (
        <div className="enhance-model-hint">
          This method uses the selected AI model, scale, and format. Denoise/detail/color sliders are for Simple local resize only.
        </div>
      )}
      <label>
        <span>Format</span>
        <select value={settings.outputFormat} onChange={(event) => patchSettings({ outputFormat: event.target.value as EnhancementSettings['outputFormat'] })}>
          <option value="png">PNG</option>
          <option value="webp">WebP</option>
          <option value="jpg">JPG</option>
        </select>
      </label>
    </div>
  );
}

function statusLabel(status: EnhancementJobStatus) {
  if (status === 'queued') return 'Queued';
  if (status === 'running') return 'Running';
  if (status === 'succeeded') return 'Done';
  if (status === 'failed') return 'Failed';
  if (status === 'deleted') return 'Deleted';
  return 'Canceled';
}

export default function EnhanceQueuePanel() {
  const { view, setView, indexToken } = useImageStore();
  const [jobs, setJobs] = useState<EnhancementJob[]>([]);
  const [error, setError] = useState('');
  const activeJobs = useMemo(() => jobs.filter((job) => job.status === 'queued' || job.status === 'running'), [jobs]);

  const refresh = useCallback(async () => {
    try {
      const res = await fetch('/api/enhance/jobs', { cache: 'no-store' });
      const data = await res.json();
      if (!res.ok) throw new Error(data.error || 'Failed to load enhancement jobs');
      setJobs(Array.isArray(data.jobs) ? data.jobs : []);
      setError('');
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }, []);

  useEffect(() => {
    void refresh();
    const onChanged = () => void refresh();
    window.addEventListener('pvu-enhance-jobs-changed', onChanged);
    return () => window.removeEventListener('pvu-enhance-jobs-changed', onChanged);
  }, [refresh]);

  useEffect(() => {
    if (activeJobs.length === 0) return;
    const id = window.setInterval(() => void refresh(), 1000);
    return () => window.clearInterval(id);
  }, [activeJobs.length, refresh]);

  const cancelJob = async (id: string) => {
    await cancelEnhancementJob(id);
    await refresh();
  };

  const retryJob = async (id: string) => {
    await fetch(`/api/enhance/jobs/${encodeURIComponent(id)}/retry`, { method: 'POST' });
    await refresh();
  };

  const deleteOutput = async (id: string) => {
    const ok = window.confirm('Delete only this enhanced output file? The original source image will not be touched.');
    if (!ok) return;
    await deleteEnhancementOutput(id);
    await refresh();
  };

  const openSource = async (sourcePath: string) => {
    const tokenParam = indexToken ? `&indexToken=${encodeURIComponent(indexToken)}` : '';
    await fetch(`/api/open?path=${encodeURIComponent(sourcePath)}${tokenParam}`, { method: 'POST' });
  };

  if (jobs.length === 0 && !error) return null;
  if (!view.enhanceQueueOpen) return null;

  return (
    <aside className="enhance-queue-panel" aria-label="Enhancement queue">
      <div className="enhance-queue-header">
        <span>Enhance queue</span>
        <div className="enhance-queue-header-actions">
          <button className="pill compact" type="button" onClick={() => void refresh()}>Refresh</button>
          <button className="pill compact" type="button" onClick={() => setView({ enhanceQueueOpen: false })}>Hide</button>
        </div>
      </div>
      {error && <div className="enhance-error">{error}</div>}
      <div className="enhance-job-list">
        {jobs.slice(0, 8).map((job) => (
          <div className={`enhance-job ${job.status}`} key={job.id}>
            <div className="enhance-job-main">
              <span className="enhance-job-name" title={job.sourcePath}>{job.sourcePath.split(/[\\/]/).pop()}</span>
              <span className="enhance-job-status">{statusLabel(job.status)} {job.progress}%</span>
            </div>
            <div className="enhance-model-hint">
              {describeJobSettings(job)}
            </div>
            {describeDiagnostics(job) && (
              <div className="enhance-model-hint" title={job.diagnostics?.notes?.join('\n') || undefined}>
                {describeDiagnostics(job)}
              </div>
            )}
            <div className="enhance-progress">
              <div style={{ width: `${Math.max(0, Math.min(100, job.progress))}%` }} />
            </div>
            {job.errorMessage && <div className="enhance-error">{job.errorMessage}</div>}
            <div className="enhance-job-actions">
              {(job.status === 'queued' || job.status === 'running') && (
                <button className="pill compact" type="button" onClick={() => void cancelJob(job.id)}>Cancel</button>
              )}
              {(job.status === 'failed' || job.status === 'canceled') && (
                <button className="pill compact" type="button" onClick={() => void retryJob(job.id)}>Retry</button>
              )}
              {job.status === 'succeeded' && (
                <a className="pill compact" href={`/api/enhance/output?jobId=${encodeURIComponent(job.id)}`} target="_blank" rel="noreferrer">
                  Open output
                </a>
              )}
              {job.status === 'succeeded' && (
                <button className="pill compact danger" type="button" onClick={() => void deleteOutput(job.id)}>Delete output</button>
              )}
              <button className="pill compact" type="button" onClick={() => void openSource(job.sourcePath)}>Source</button>
            </div>
          </div>
        ))}
      </div>
    </aside>
  );
}
