| Method | Kind | Model MB | IoU | IoU, holes filled | Boundary F, holes filled | Worst IoU, filled | Seconds per image (CPU) |
|---|---|---|---|---|---|---|---|
| border_median | classical | 0 | 0.581 | 0.656 | 0.538 | 0.431 | 0.08 |
| shadow_aware | classical | 0 | 0.580 | 0.596 | 0.664 | 0.052 | 0.31 |
| shadow_palette | classical | 0 | 0.822 | 0.837 | 0.818 | 0.052 | 1.50 |
| shadow_palette_grabcut | classical | 0 | 0.841 | 0.875 | 0.864 | 0.052 | 2.06 |
| flood_shadow | classical | 0 | 0.539 | 0.532 | 0.501 | 0.000 | 0.14 |
| flood_grabcut | classical | 0 | 0.546 | 0.531 | 0.561 | 0.000 | 0.90 |
| grabcut | classical | 0 | 0.278 | 0.360 | 0.309 | 0.026 | 2.99 |
| shadow_grabcut | classical | 0 | 0.587 | 0.627 | 0.710 | 0.052 | 1.27 |
| u2netp | network | 4.4 | 0.728 | 0.850 | 0.820 | 0.337 | 0.29 |
| silueta | network | 43 | 0.749 | 0.884 | 0.829 | 0.666 | 1.05 |
| isnet | network | 171 | 0.940 | 0.955 | 0.966 | 0.662 | 1.74 |
| birefnet_lite | network | 214 | 0.960 | 0.976 | 0.975 | 0.740 | 15.05 |
| mobilesam_autobox | sam | 43 | 0.814 | 0.927 | 0.929 | 0.356 | 2.27 |
| sam21_tiny_autobox | sam | 148 | 0.794 | 0.903 | 0.876 | 0.346 | 3.93 |
| mobilesam_u2netp_box | sam | 47.4 | 0.808 | 0.921 | 0.924 | 0.685 | 1.46 |
| mobilesam_click | sam-click | 43 | 0.623 | 0.699 | 0.674 | 0.007 | 1.11 |
| sam21_tiny_click | sam-click | 148 | 0.580 | 0.591 | 0.582 | 0.006 | 2.56 |
| mobilesam_click_largest | sam-click | 43 | 0.846 | 0.957 | 0.921 | 0.765 | 1.19 |
| sam21_tiny_click_largest | sam-click | 148 | 0.851 | 0.833 | 0.778 | 0.173 | 2.74 |
