import React, { useState } from 'react';
import { createRoot } from 'react-dom/client';
import './site.css';

function Gallery({ imageUrl }) {
  const [theme, setTheme] = useState('dark');
  return <div className="gallery">
    <div className="theme-switch" role="group" aria-label="Preview appearance">
      <button type="button" aria-pressed={theme === 'light'} onClick={() => setTheme('light')}>☀ Light</button>
      <button type="button" aria-pressed={theme === 'dark'} onClick={() => setTheme('dark')}>☾ Dark</button>
    </div>
    <div className="gallery-layout">
      <div className="gallery-copy"><span className="gallery-step">01 / ORGANIZE</span><h3>Everything in<br />its own place.</h3><p>From your everyday accounts to your most personal notes, give each secret a home.</p><ul><li>Groups that make sense to you</li><li>Custom fields for the details</li><li>Search by names and field labels</li></ul></div>
      <figure className="gallery-figure"><div className={`phone-crop ${theme}-preview`}><img src={imageUrl} width="1536" height="1024" alt={`PassCrate ${theme} design preview showing the Personal group with email, banking, and private note entries.`} loading="lazy" /></div><figcaption aria-live="polite">{theme === 'dark' ? 'Dark' : 'Light'} appearance · Design preview</figcaption></figure>
      <div className="gallery-aside"><span className="feature-icon">✧</span><h3>A calmer kind<br />of everyday.</h3><p>Thoughtful details. A familiar layout. A little less friction between you and what you need.</p><span className="small-label">MADE FOR YOUR POCKET</span></div>
    </div>
    <p className="preview-disclaimer">Design previews from the PassCrate project. Final app screens may differ.</p>
  </div>;
}
const gallery = document.getElementById('app-gallery');
if (gallery) createRoot(gallery).render(<Gallery imageUrl={gallery.dataset.previewUrl} />);
